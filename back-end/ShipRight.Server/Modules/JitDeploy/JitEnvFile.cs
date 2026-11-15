using System.Text;
using System.Text.RegularExpressions;
using ShipRight.Modules.Projects;

namespace ShipRight.Modules.JitDeploy;

/// <summary>How many variables each side supplies, and which of the server's the project replaces. Names only, never values.</summary>
public sealed record EnvSummary(int FromServer, int FromProject, IReadOnlyList<string> Overridden);

/// <summary>
/// The variables a build-on-server deploy hands to compose (the <c>${VAR}</c> values in the compose file). The server's own
/// settings and the project's are merged by variable name with the project's winning, then written to one private file
/// beside the deploy state on the server. That folder is outside the git checkout, so the checkout's clean-up cannot remove
/// it. Values are never interpreted (no unquoting, no expansion): lines are kept exactly as typed.
/// </summary>
public static class JitEnvFile
{
    public const int MaxChars = 64_000;

    private static readonly Regex Assignment = new(@"^\s*(?:export\s+)?([A-Za-z_][A-Za-z0-9_.\-]*)\s*(=(.*))?$", RegexOptions.Compiled);

    /// <summary>One variable (possibly spanning several lines when its quoted value does), or a comment/stray line with no key.</summary>
    private sealed record Entry(string? Key, List<string> Lines);

    public static string Path(JitTarget t) => t.StateDir + "/deploy.env";

    /// <summary>The merged file, or null when neither side defines a variable (then no file is written and no flag is passed).</summary>
    public static string? Merge(string? server, string? project)
    {
        var s = Parse(server);
        var p = Parse(project);
        if (!s.Any(e => e.Key is not null) && !p.Any(e => e.Key is not null)) return null;

        var projectKeys = p.Where(e => e.Key is not null).Select(e => e.Key!).ToHashSet(StringComparer.Ordinal);
        var keptServer = s.Where(e => e.Key is null || !projectKeys.Contains(e.Key)).ToList();

        var sb = new StringBuilder();
        if (keptServer.Any(e => e.Key is not null))
        {
            sb.Append("# From the server settings\n");
            foreach (var e in keptServer) foreach (var l in e.Lines) sb.Append(l).Append('\n');
        }
        if (p.Any(e => e.Key is not null))
        {
            sb.Append("# From this project (these win over the server settings)\n");
            foreach (var e in p) foreach (var l in e.Lines) sb.Append(l).Append('\n');
        }
        return sb.ToString();
    }

    public static IEnumerable<string> KeysOf(string? text) =>
        Parse(text).Where(e => e.Key is not null).Select(e => e.Key!).Distinct(StringComparer.Ordinal);

    public static EnvSummary Summarise(string? server, string? project)
    {
        var s = KeysOf(server).ToList();
        var p = KeysOf(project).ToHashSet(StringComparer.Ordinal);
        var overridden = s.Where(p.Contains).ToList();
        return new EnvSummary(s.Count - overridden.Count, p.Count, overridden);
    }

    /// <summary>A plain sentence for the deploy log. Names only.</summary>
    public static string Describe(EnvSummary s)
    {
        var text = $"{s.FromServer} from the server settings, {s.FromProject} from this project";
        if (s.Overridden.Count > 0) text += $" (overriding: {string.Join(", ", s.Overridden)})";
        return text + ".";
    }

    /// <summary>Plain-language reason the text cannot be used, or null. Never repeats the text.</summary>
    public static string? Problem(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        if (text.Length > MaxChars) return $"The variables are too large (over {MaxChars:N0} characters). Keep only what the app needs.";
        if (text.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
            return "The variables contain a character that is not allowed (a hidden control character). Retype the line it came from.";
        return null;
    }

    public static string WriteCommand(JitTarget t, string content)
    {
        JitNames.Path(t.ProjectDir);
        var text = content.Replace("\r\n", "\n");
        if (!text.EndsWith('\n')) text += "\n";
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
        var file = ShellQuote.Single(Path(t));
        var tmp = ShellQuote.Single(Path(t) + ".tmp");
        return $"mkdir -p {ShellQuote.Single(t.StateDir)} && umask 077 && echo '{b64}' | base64 -d > {tmp} && chmod 600 {tmp} && mv -f {tmp} {file}";
    }

    private static List<Entry> Parse(string? text)
    {
        var entries = new List<Entry>();
        if (string.IsNullOrWhiteSpace(text)) return entries;

        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Trim().Length == 0) continue;
            if (line.TrimStart().StartsWith('#')) { entries.Add(new Entry(null, [line])); continue; }

            var m = Assignment.Match(line);
            if (!m.Success) { entries.Add(new Entry(null, [line])); continue; }

            var block = new List<string> { line };
            if (m.Groups[3].Success && OpensQuote(m.Groups[3].Value, out var quote) && !Closes(m.Groups[3].Value.TrimStart()[1..], quote))
            {
                while (i + 1 < lines.Length)
                {
                    block.Add(lines[++i]);
                    if (Closes(lines[i], quote)) break;
                }
            }
            entries.Add(new Entry(m.Groups[1].Value, block));
        }
        return entries;
    }

    private static bool OpensQuote(string value, out char quote)
    {
        var v = value.TrimStart();
        quote = v.Length > 0 && (v[0] == '"' || v[0] == '\'') ? v[0] : '\0';
        return quote != '\0';
    }

    /// <summary>True when the text contains the closing quote (a double quote escaped with a backslash does not count).</summary>
    private static bool Closes(string text, char quote)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\' && quote == '"') { i++; continue; }
            if (text[i] == quote) return true;
        }
        return false;
    }
}

/// <summary>
/// Turns a project plus its build-on-server settings into a deploy request that also carries the merged variables. A thin
/// layer over <see cref="JitDeployMapper"/>, which stays exactly as it was.
/// </summary>
public static class JitEnvMapper
{
    public static JitMapResult Map(ProjectConfig project, JitDeployConfig? cfg, string? commit = null, bool force = false, bool allowWithoutRollback = false)
    {
        var map = JitDeployMapper.Map(project, cfg, commit, force, allowWithoutRollback);
        if (!map.Ok || cfg is null) return map;
        if (!string.IsNullOrWhiteSpace(cfg.EnvFile)) return map;   // the person keeps the file on the server: it is used as it is

        var merged = JitEnvFile.Merge(project.Server.EnvFile, cfg.EnvText);
        if (merged is null) return map;

        var request = map.Request! with
        {
            EnvFile = JitEnvFile.Path(map.Request!.Target),
            EnvContent = merged,
            EnvNote = JitEnvFile.Describe(JitEnvFile.Summarise(project.Server.EnvFile, cfg.EnvText)),
        };
        return new JitMapResult(request, map.Problems);
    }
}

/// <summary>The settings checks, plus a check of the project's variables. The original validator is unchanged.</summary>
public static class JitEnvConfigValidator
{
    public static IReadOnlyList<JitConfigProblem> Validate(JitDeployConfig c)
    {
        var problems = JitDeployConfigValidator.Validate(c).ToList();
        if (JitEnvFile.Problem(c.EnvText) is { } text) problems.Add(new JitConfigProblem(text, "envText"));
        return problems;
    }
}
