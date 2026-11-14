using System.Text.RegularExpressions;
using Serilog;
using ShipRight.Modules.Projects;
using ShipRight.Modules.Setup;

namespace ShipRight.Modules.DockerAdoption;

public enum ComposeMode { Registry, BuildOnServer }

public sealed record ComposeChange(string Service, string Kind, string Before, string After);

public sealed record ComposeProposal(string NewText, IReadOnlyList<ComposeChange> Changes, IReadOnlyList<string> Notes);

/// <summary>
/// The smallest edits to an existing compose file so ShipRight can deploy it: each service ShipRight builds gets its own tag
/// variable on its image line (and, for build-on-server, a build section with the recipe). Services ShipRight does not build
/// are never touched, every other line stays byte for byte, and nothing is written here: the edits are only proposed.
/// </summary>
public static class ComposeAdvice
{
    public static ComposeProposal Propose(string root, AdoptionPlan plan, ComposeMode mode)
    {
        var notes = new List<string>();
        if (plan.ComposeFile is null)
            return new ComposeProposal("", [], ["There is no compose file, so there is nothing to change."]);

        var path = Path.Combine(root, plan.ComposeFile.Replace('/', Path.DirectorySeparatorChar));
        var original = File.ReadAllText(path);
        var nl = original.Contains("\r\n") ? "\r\n" : "\n";
        var lines = original.Split(nl).ToList();
        var composeDir = Path.GetDirectoryName(path)!;
        var blocks = Blocks(lines);

        var edits = new List<(int Index, bool InsertAfter, string[] New)>();
        var changes = new List<ComposeChange>();
        var replacedShared = new List<string>();

        foreach (var s in plan.Services)
        {
            if (s.ComposeService is null) continue;
            if (s.DockerfilePath is null)
            {
                notes.Add($"{s.Name} was skipped: ShipRight could not tell which Dockerfile builds it. Pick one first.");
                continue;
            }
            if (!blocks.TryGetValue(s.ComposeService, out var block)) continue;

            foreach (var a in s.ArgsNeedingAValue)
                notes.Add($"{s.Name}: the Dockerfile needs a build argument {a} and none has a value. Give it one, or the image may be built wrong.");

            var variable = ShipRight.Modules.Setup.GeneratedCompose.TagVariable(s.Name);
            var pad = new string(' ', block.FieldIndent);

            // image line: its own tag variable
            if (block.ImageIndex is { } img)
            {
                var current = lines[img];
                if (s.TagStyle != TagStyle.PerServiceVariable)
                {
                    var repo = s.ImageName ?? DockerSetupAnalyzer.ImageName(ValueOf(current)) ?? "";
                    var after = $"{pad}image: ${{{variable}:-{repo}:{DefaultTag(ValueOf(current))}}}";
                    edits.Add((img, false, [after]));
                    changes.Add(new ComposeChange(s.Name, "image", current.Trim(), after.Trim()));
                    if (s.TagStyle == TagStyle.SharedVariable) replacedShared.Add($"{s.Name} ({variable})");
                }
            }
            else if (s.ImageName is null)
                notes.Add($"{s.Name} has no image name in the compose file, so ShipRight cannot give it a tag variable.");

            // build-on-server: the recipe, written as a build section
            if (mode == ComposeMode.BuildOnServer)
            {
                if (block.HasBuild)
                    notes.Add($"{s.Name} already has a build section. Check that it uses {s.DockerfilePath}.");
                else
                {
                    var ctx = Path.GetRelativePath(composeDir, Path.GetFullPath(Path.Combine(plan.Root, s.ContextRelative.Replace('/', Path.DirectorySeparatorChar)))).Replace('\\', '/');
                    var build = new List<string> { $"{pad}build:", $"{pad}  context: {(ctx is "" ? "." : ctx)}", $"{pad}  dockerfile: {s.DockerfilePath}" };
                    if (s.Target is not null) build.Add($"{pad}  target: {s.Target}");
                    var args = s.BuildArgs.ToDictionary(k => k.Key, k => k.Value);
                    foreach (var a in s.ArgsNeedingAValue) args.TryAdd(a, "");
                    if (args.Count > 0)
                    {
                        build.Add($"{pad}  args:");
                        foreach (var (k, v) in args.OrderBy(x => x.Key, StringComparer.Ordinal))
                            build.Add($"{pad}    {k}: \"{v.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"");
                    }
                    edits.Add((block.HeaderIndex, true, [.. build]));
                    changes.Add(new ComposeChange(s.Name, "build", "(no build section)", string.Join(" / ", build.Select(b => b.Trim()))));
                }
            }
        }

        if (replacedShared.Count > 0)
            notes.Add($"{string.Join(", ", replacedShared)} now use their own tag variable instead of a shared one, so each can be versioned independently. Any script that sets the shared variable should be updated.");

        // applied from the bottom up so earlier line numbers stay valid
        foreach (var e in edits.OrderByDescending(e => e.Index).ThenByDescending(e => e.InsertAfter ? 0 : 1))
        {
            if (e.InsertAfter) lines.InsertRange(e.Index + 1, e.New);
            else lines[e.Index] = e.New[0];
        }
        return new ComposeProposal(string.Join(nl, lines), changes, notes);
    }

    private sealed record Block(int HeaderIndex, int FieldIndent, int? ImageIndex, bool HasBuild);

    private static Dictionary<string, Block> Blocks(List<string> lines)
    {
        var result = new Dictionary<string, Block>(StringComparer.Ordinal);
        int Indent(string l) => l.Length - l.TrimStart(' ').Length;
        bool Skippable(string l) => l.Trim().Length == 0 || l.TrimStart().StartsWith('#');

        var start = lines.FindIndex(l => l.TrimEnd() == "services:");
        if (start < 0) return result;
        var first = lines.Skip(start + 1).Select((l, i) => (l, i: i + start + 1)).FirstOrDefault(x => !Skippable(x.l));
        if (first.l is null) return result;
        var child = Indent(first.l);
        if (child == 0) return result;

        var headers = new List<(string Name, int Index)>();
        var end = lines.Count;
        for (var i = start + 1; i < lines.Count; i++)
        {
            if (Skippable(lines[i])) continue;
            var ind = Indent(lines[i]);
            if (ind == 0) { end = i; break; }
            var m = Regex.Match(lines[i], @"^ {" + child + @"}(?<n>[^\s:#-][^:#]*):\s*(#.*)?$");
            if (ind == child && m.Success) headers.Add((m.Groups["n"].Value.Trim().Trim('"', '\''), i));
        }

        for (var h = 0; h < headers.Count; h++)
        {
            var from = headers[h].Index + 1;
            var to = h + 1 < headers.Count ? headers[h + 1].Index : end;
            var field = -1; int? image = null; var hasBuild = false;
            for (var i = from; i < to; i++)
            {
                if (Skippable(lines[i])) continue;
                if (field < 0) field = Indent(lines[i]);
                if (Indent(lines[i]) != field) continue;
                var t = lines[i].TrimStart();
                if (t.StartsWith("image:")) image = i;
                else if (t.StartsWith("build:")) hasBuild = true;
            }
            result[headers[h].Name] = new Block(headers[h].Index, field < 0 ? child + 2 : field, image, hasBuild);
        }
        return result;
    }

    private static string ValueOf(string line)
    {
        var v = line[(line.IndexOf(':') + 1)..];
        var hash = Regex.Match(v, @"\s#");
        if (hash.Success) v = v[..hash.Index];
        v = v.Trim();
        return v.Length >= 2 && ((v[0] == '"' && v[^1] == '"') || (v[0] == '\'' && v[^1] == '\'')) ? v[1..^1] : v;
    }

    /// <summary>The tag the image uses now: the default inside <c>${VAR:-tag}</c>, a literal tag, or <c>latest</c>.</summary>
    private static string DefaultTag(string image)
    {
        var m = Regex.Match(image, @":\$\{[A-Za-z_][A-Za-z0-9_]*:?-(?<t>[^}]*)\}$");
        if (m.Success) return m.Groups["t"].Value.Length > 0 ? m.Groups["t"].Value : "latest";
        var colon = image.LastIndexOf(':');
        return colon > image.LastIndexOf('/') && !image.StartsWith("${") ? image[(colon + 1)..] : "latest";
    }
}

public sealed record VersionFilesResult(IReadOnlyList<string> Created, IReadOnlyList<string> AlreadyThere, IReadOnlyList<string> Unknown);

public sealed record ComposeApplyResult(bool Written, string? BackupFile, IReadOnlyList<ComposeChange> Changes, IReadOnlyList<string> Notes);

/// <summary>The only writes adoption makes, each create-only or backed up, and always computed here from the folder, never from a caller's path.</summary>
public static class AdoptionApply
{
    public const string FirstVersion = "0.1.0";

    public static VersionFilesResult CreateVersionFiles(string root, IEnumerable<string> serviceNames)
    {
        var plan = DockerSetupAnalyzer.Analyse(root);
        var created = new List<string>(); var there = new List<string>(); var unknown = new List<string>();
        foreach (var name in serviceNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var s = plan.Services.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (s is null) { unknown.Add(name); continue; }
            var rel = s.VersionFile ?? s.SuggestedVersionFile;
            var full = Path.GetFullPath(Path.Combine(plan.Root, rel.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(plan.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) { unknown.Add(name); continue; }
            if (File.Exists(full)) { there.Add(rel); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, FirstVersion + "\n");
            created.Add(rel);
            Log.Information("Adoption created {File}", rel);
        }
        return new VersionFilesResult(created, there, unknown);
    }

    public static ComposeApplyResult ApplyCompose(string root, ComposeMode mode, IReadOnlyDictionary<string, string>? choices = null)
    {
        var plan = DockerSetupAnalyzer.Analyse(root, choices);
        var proposal = ComposeAdvice.Propose(plan.Root, plan, mode);
        if (plan.ComposeFile is null || proposal.Changes.Count == 0)
            return new ComposeApplyResult(false, null, proposal.Changes, proposal.Notes);

        var path = Path.Combine(plan.Root, plan.ComposeFile.Replace('/', Path.DirectorySeparatorChar));
        var backup = path + ".shipright-backup";
        for (var n = 2; File.Exists(backup); n++) backup = $"{path}.shipright-backup-{n}";
        File.Copy(path, backup);
        File.WriteAllText(path, proposal.NewText);
        Log.Information("Adoption rewrote {File} ({Count} changes), original kept as {Backup}", plan.ComposeFile, proposal.Changes.Count, Path.GetFileName(backup));
        return new ComposeApplyResult(true, Path.GetFileName(backup), proposal.Changes, proposal.Notes);
    }
}

public sealed record AdoptRequest(string? RootPath, string? Mode, Dictionary<string, string>? Choices = null);

/// <summary>Endpoints for adopting an existing Docker setup. Planning and previewing only read; the two writes are create-only or backed up.</summary>
public static class AdoptionRoutes
{
    public static void MapAdoptionRoutes(this WebApplication app)
    {
        app.MapPost("/api/setup/adopt/plan", (AdoptRequest req) => HandlePlan(req));
        app.MapPost("/api/setup/adopt/compose-preview", (AdoptRequest req) => HandleComposePreview(req));
        app.MapPost("/api/setup/adopt/compose-apply", (AdoptRequest req) => HandleComposeApply(req));
        app.MapPost("/api/setup/adopt/version-files", (VersionFilesRequest req) => HandleVersionFiles(req));
    }

    public sealed record VersionFilesRequest(string? RootPath, string[]? Services);

    private static object Err(string message, string? field = null) => ProjectRouter.Error(message, field);

    private static IResult? Check(string? root) =>
        string.IsNullOrWhiteSpace(root) ? Results.BadRequest(Err("Choose a folder first.", "rootPath"))
        : !Directory.Exists(root) ? Results.BadRequest(Err("That folder does not exist.", "rootPath"))
        : null;

    private static bool TryMode(string? mode, out ComposeMode m)
    {
        m = mode switch { "registry" => ComposeMode.Registry, "build-on-server" => ComposeMode.BuildOnServer, _ => default };
        return mode is "registry" or "build-on-server";
    }

    internal static IResult HandlePlan(AdoptRequest req) =>
        Check(req.RootPath) ?? Results.Ok(new { plan = DockerSetupAnalyzer.Analyse(req.RootPath!, req.Choices) });

    internal static IResult HandleComposePreview(AdoptRequest req)
    {
        if (Check(req.RootPath) is { } bad) return bad;
        if (!TryMode(req.Mode, out var mode)) return Results.BadRequest(Err("Say whether the compose file is for a registry or for building on the server.", "mode"));
        var plan = DockerSetupAnalyzer.Analyse(req.RootPath!, req.Choices);
        var p = ComposeAdvice.Propose(plan.Root, plan, mode);
        return Results.Ok(new { composeFile = plan.ComposeFile, changes = p.Changes, notes = p.Notes, newText = p.NewText });
    }

    internal static IResult HandleComposeApply(AdoptRequest req)
    {
        if (Check(req.RootPath) is { } bad) return bad;
        if (!TryMode(req.Mode, out var mode)) return Results.BadRequest(Err("Say whether the compose file is for a registry or for building on the server.", "mode"));
        return Results.Ok(AdoptionApply.ApplyCompose(req.RootPath!, mode, req.Choices));
    }

    internal static IResult HandleVersionFiles(VersionFilesRequest req)
    {
        if (Check(req.RootPath) is { } bad) return bad;
        return Results.Ok(AdoptionApply.CreateVersionFiles(req.RootPath!, req.Services ?? []));
    }
}
