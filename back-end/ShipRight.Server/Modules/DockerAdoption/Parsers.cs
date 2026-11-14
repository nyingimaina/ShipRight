using System.Text.Json;
using System.Text.RegularExpressions;

namespace ShipRight.Modules.DockerAdoption;

public sealed record DockerArg(string Name, bool HasDefault);

/// <summary>What ShipRight needs to know about a Dockerfile: its stages, what it copies from the build context, and its arguments.</summary>
public sealed record DockerfileInfo(
    IReadOnlyList<string> Stages,
    string? FinalStage,
    IReadOnlyList<string> OtherEnds,
    IReadOnlyList<string> CopySources,
    bool CopiesEverything,
    IReadOnlyList<DockerArg> Args);

/// <summary>A tolerant reader of Dockerfiles. It only collects facts; it never decides whether a Dockerfile is valid.</summary>
public static class DockerfileParser
{
    private static readonly Regex Heredoc = new(@"<<-?\s*[""']?(?<tag>\w+)[""']?", RegexOptions.Compiled);

    public static DockerfileInfo Parse(string text)
    {
        var stageNames = new List<string?>();
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sources = new List<string>();
        var args = new List<DockerArg>();
        var everything = false;
        string? skipUntil = null;

        foreach (var line in LogicalLines(text))
        {
            if (skipUntil is not null)
            {
                if (line.Trim() == skipUntil) skipUntil = null;
                continue;
            }

            var space = line.IndexOfAny([' ', '\t']);
            var word = (space < 0 ? line : line[..space]).ToUpperInvariant();
            var rest = space < 0 ? "" : line[(space + 1)..].Trim();

            switch (word)
            {
                case "FROM":
                {
                    var tokens = rest.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).Where(t => !t.StartsWith("--")).ToList();
                    if (tokens.Count > 0) referenced.Add(tokens[0]);
                    var asAt = tokens.FindIndex(t => t.Equals("AS", StringComparison.OrdinalIgnoreCase));
                    stageNames.Add(asAt >= 0 && asAt + 1 < tokens.Count ? tokens[asAt + 1] : null);
                    break;
                }
                case "COPY":
                case "ADD":
                {
                    if (Heredoc.Match(rest) is { Success: true } h) { skipUntil = h.Groups["tag"].Value; break; }
                    var tokens = new List<string>();
                    string? from = null;
                    if (rest.StartsWith('['))
                    {
                        try { tokens.AddRange(JsonSerializer.Deserialize<string[]>(rest) ?? []); }
                        catch (JsonException) { break; }
                    }
                    else
                    {
                        foreach (var t in rest.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
                        {
                            if (t.StartsWith("--from=", StringComparison.OrdinalIgnoreCase)) from = t[7..];
                            else if (t.StartsWith("--")) continue;
                            else tokens.Add(t);
                        }
                    }
                    if (from is not null) { referenced.Add(from); break; }
                    foreach (var s in tokens.Take(Math.Max(0, tokens.Count - 1)))
                    {
                        if (Regex.IsMatch(s, @"^(https?://|git@|ftp://)", RegexOptions.IgnoreCase)) continue;
                        sources.Add(s);
                        if (s is "." or "./") everything = true;
                    }
                    break;
                }
                case "ARG":
                {
                    var eq = rest.IndexOf('=');
                    var name = (eq < 0 ? rest : rest[..eq]).Trim();
                    if (name.Length > 0) args.Add(new DockerArg(name, eq >= 0));
                    break;
                }
            }
        }

        var named = stageNames.Where(n => n is not null).Select(n => n!).ToList();
        var final = stageNames.Count > 0 ? stageNames[^1] : null;
        var ends = named.Where(n => !n.Equals(final, StringComparison.OrdinalIgnoreCase) && !referenced.Contains(n)).ToList();
        return new DockerfileInfo(named, final, ends, sources, everything, args);
    }

    /// <summary>Instruction lines with continuations joined; comments and blank lines dropped.</summary>
    private static IEnumerable<string> LogicalLines(string text)
    {
        var current = "";
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (current.Length > 0 && (line.TrimStart().StartsWith('#') || line.Length == 0)) continue;
            if (current.Length == 0 && (line.TrimStart().StartsWith('#') || line.Trim().Length == 0)) continue;
            if (line.EndsWith('\\')) { current += line[..^1].Trim() + " "; continue; }
            yield return (current + line.Trim()).Trim();
            current = "";
        }
        if (current.Trim().Length > 0) yield return current.Trim();
    }
}

public sealed record ComposeBuild(string? Context, string? Dockerfile, string? Target, IReadOnlyDictionary<string, string> Args);

public sealed record ComposeServiceInfo(string Name, string? Image, ComposeBuild? Build, IReadOnlyList<string> DependsOn);

/// <summary>
/// Reads the parts of a compose file ShipRight cares about (each service's image, build and depends_on) by indentation,
/// without a YAML library. Anything it does not understand is ignored; it never throws.
/// </summary>
public static class ComposeLite
{
    private sealed record Line(int Indent, string Text);

    public static IReadOnlyList<ComposeServiceInfo> Parse(string text)
    {
        try { return ParseCore(text); }
        catch (Exception) { return []; }
    }

    private static IReadOnlyList<ComposeServiceInfo> ParseCore(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n')
            .Select(l => new Line(l.Length - l.TrimStart(' ').Length, StripComment(l).Trim()))
            .Where(l => l.Text.Length > 0)
            .ToList();

        var start = lines.FindIndex(l => l.Indent == 0 && l.Text == "services:");
        if (start < 0 || start + 1 >= lines.Count) return [];
        var childIndent = lines[start + 1].Indent;
        if (childIndent <= 0) return [];

        var services = new List<ComposeServiceInfo>();
        string? name = null;
        var body = new List<Line>();
        void Flush() { if (name is not null) services.Add(Build(name, body)); body = []; }

        for (var i = start + 1; i < lines.Count; i++)
        {
            var l = lines[i];
            if (l.Indent == 0) break;
            if (l.Indent == childIndent && !l.Text.StartsWith('-') && l.Text.EndsWith(':'))
            {
                Flush();
                name = Unquote(l.Text[..^1]);
            }
            else if (l.Indent > childIndent) body.Add(l);
        }
        Flush();
        return services;
    }

    private static ComposeServiceInfo Build(string name, List<Line> body)
    {
        string? image = null;
        ComposeBuild? build = null;
        var depends = new List<string>();
        if (body.Count == 0) return new(name, null, null, depends);

        var fieldIndent = body[0].Indent;
        for (var i = 0; i < body.Count; i++)
        {
            var l = body[i];
            if (l.Indent != fieldIndent) continue;
            var (key, value) = Split(l.Text);
            var sub = SubBlock(body, i);
            switch (key)
            {
                case "image": image = Unquote(value); break;
                case "build": build = ParseBuild(value, sub); break;
                case "depends_on": depends.AddRange(ParseDepends(value, sub)); break;
            }
        }
        return new(name, image, build, depends);
    }

    private static ComposeBuild ParseBuild(string inline, List<Line> sub)
    {
        if (inline.Length > 0) return new(Unquote(inline), null, null, new Dictionary<string, string>());
        string? context = null, dockerfile = null, target = null;
        var args = new Dictionary<string, string>();
        if (sub.Count == 0) return new(context, dockerfile, target, args);

        var indent = sub[0].Indent;
        for (var i = 0; i < sub.Count; i++)
        {
            if (sub[i].Indent != indent) continue;
            var (key, value) = Split(sub[i].Text);
            switch (key)
            {
                case "context": context = Unquote(value); break;
                case "dockerfile": dockerfile = Unquote(value); break;
                case "target": target = Unquote(value); break;
                case "args":
                    foreach (var a in SubBlock(sub, i))
                    {
                        var t = a.Text.StartsWith("- ") ? a.Text[2..].Trim() : a.Text;
                        var eq = a.Text.StartsWith("- ") ? t.IndexOf('=') : t.IndexOf(':');
                        var k = Unquote(eq < 0 ? t : t[..eq]);
                        if (k.Length > 0) args[k] = eq < 0 ? "" : Unquote(t[(eq + 1)..]);
                    }
                    break;
            }
        }
        return new(context, dockerfile, target, args);
    }

    private static IEnumerable<string> ParseDepends(string inline, List<Line> sub)
    {
        if (inline.StartsWith('['))
            return inline.Trim('[', ']').Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Unquote);
        if (sub.Count == 0) return [];
        var indent = sub[0].Indent;
        return sub.Where(s => s.Indent == indent)
            .Select(s => s.Text.StartsWith("- ") ? Unquote(s.Text[2..]) : Unquote(Split(s.Text).Key))
            .Where(s => s.Length > 0).ToList();
    }

    private static List<Line> SubBlock(List<Line> lines, int index)
    {
        var indent = lines[index].Indent;
        var sub = new List<Line>();
        for (var i = index + 1; i < lines.Count && lines[i].Indent > indent; i++) sub.Add(lines[i]);
        return sub;
    }

    private static (string Key, string Value) Split(string text)
    {
        var colon = text.IndexOf(':');
        if (colon < 0) return (text.Trim(), "");
        return (text[..colon].Trim(), text[(colon + 1)..].Trim());
    }

    private static string Unquote(string v)
    {
        v = v.Trim();
        return v.Length >= 2 && ((v[0] == '"' && v[^1] == '"') || (v[0] == '\'' && v[^1] == '\'')) ? v[1..^1] : v;
    }

    private static string StripComment(string line)
    {
        bool single = false, dbl = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '\'' && !dbl) single = !single;
            else if (c == '"' && !single) dbl = !dbl;
            else if (c == '#' && !single && !dbl && (i == 0 || char.IsWhiteSpace(line[i - 1]))) return line[..i];
        }
        return line;
    }
}
