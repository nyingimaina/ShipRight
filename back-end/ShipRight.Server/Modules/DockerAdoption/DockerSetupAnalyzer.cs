using System.Text.RegularExpressions;
using ShipRight.Modules.Setup;

namespace ShipRight.Modules.DockerAdoption;

public enum TagStyle { None, Literal, SharedVariable, PerServiceVariable }

/// <summary>One image a project builds, as found in its existing Docker setup.</summary>
/// <param name="DockerfilePath">Relative to the build context; null when it could not be decided (see <see cref="Alternatives"/>).</param>
/// <param name="ContextRelative">The build context relative to the project folder; "." is the folder itself.</param>
public sealed record AdoptedService(
    string Name,
    string? DockerfilePath,
    string ContextRelative,
    string? Target,
    IReadOnlyDictionary<string, string> BuildArgs,
    string? ImageName,
    string? ComposeService,
    Confidence Confidence,
    IReadOnlyList<string> Alternatives,
    IReadOnlyList<string> OtherStages,
    IReadOnlyList<string> ArgsNeedingAValue,
    TagStyle TagStyle,
    string? TagVariable,
    IReadOnlyList<string> Reasons);

public sealed record AdoptionPlan(
    string Root,
    string? ComposeFile,
    IReadOnlyList<AdoptedService> Services,
    IReadOnlyList<string> ThirdParty,
    IReadOnlyList<string> UnusedDockerfiles,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Reads an existing Docker setup (Dockerfiles with any name, and a compose file) and works out, for each image the
/// project builds, which Dockerfile makes it, from which folder, which stage, and with which arguments. Defaults are
/// inferred (a compose build section wins, then names, then what the Dockerfile copies); anything it cannot decide is
/// left undecided with the candidates listed, never guessed. It only reads files.
/// </summary>
public static class DockerSetupAnalyzer
{
    private static readonly string[] SkippedFolders = [".git", "node_modules", "bin", "obj", ".next", "dist", "vendor", ".venv", "__pycache__", "coverage", ".idea", ".vs"];
    private static readonly string[] ComposeNames = ["docker-compose.yml", "docker-compose.yaml", "compose.yml", "compose.yaml"];
    private const int MaxDepth = 5;

    internal sealed record Found(string Full, string Rel, int Depth, string? Stem, string? Folder, DockerfileInfo Info);

    public static AdoptionPlan Analyse(string rootPath)
    {
        var root = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"Folder not found: {rootPath}");

        var warnings = new List<string>();
        var files = Walk(root, root, 0).ToList();
        var dockerfiles = files.Where(IsDockerfile).Select(f => Read(root, f)).OrderBy(f => f.Rel, StringComparer.OrdinalIgnoreCase).ToList();
        var composeFiles = files.Where(f => ComposeNames.Contains(Path.GetFileName(f), StringComparer.OrdinalIgnoreCase))
            .Select(f => (Full: f, Rel: Rel(root, f), Depth: Depth(root, f)))
            .OrderBy(c => c.Depth).ThenBy(c => c.Rel, StringComparer.OrdinalIgnoreCase).ToList();

        if (dockerfiles.Count == 0)
            warnings.Add("No Dockerfile was found in this folder or the folders below it.");

        var compose = composeFiles.Count > 0 ? composeFiles[0] : default;
        foreach (var other in composeFiles.Skip(1))
            warnings.Add($"Also found another compose file at {other.Rel}. ShipRight used {compose.Rel}.");

        var services = new List<AdoptedService>();
        var thirdParty = new List<string>();
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (composeFiles.Count > 0)
        {
            var composeDir = Path.GetDirectoryName(compose.Full)!;
            foreach (var cs in ComposeLite.Parse(File.ReadAllText(compose.Full)))
            {
                var adopted = cs.Build is not null
                    ? FromComposeBuild(root, composeDir, cs, dockerfiles, claimed, warnings)
                    : FromNames(root, cs, dockerfiles, claimed);
                if (adopted is null) thirdParty.Add(cs.Name); else services.Add(adopted);
            }
        }
        else
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in dockerfiles)
            {
                claimed.Add(f.Full);
                services.Add(FromDockerfile(root, f, Unique(NameFor(root, f), names)));
            }
        }

        var shared = services.Where(s => s.TagStyle == TagStyle.SharedVariable).GroupBy(s => s.TagVariable).FirstOrDefault(g => g.Count() > 1);
        if (shared is not null)
            warnings.Add($"{string.Join(", ", shared.Select(s => s.Name))} share one tag variable ({shared.Key}), so they cannot be versioned independently.");

        var unused = dockerfiles.Where(f => !claimed.Contains(f.Full)).Select(f => f.Rel).ToList();
        return new AdoptionPlan(root, composeFiles.Count > 0 ? compose.Rel : null, services, thirdParty, unused, warnings);
    }

    // ── services from a compose build section ────────────────────────────────

    private static AdoptedService? FromComposeBuild(string root, string composeDir, ComposeServiceInfo cs, List<Found> all, HashSet<string> claimed, List<string> warnings)
    {
        var b = cs.Build!;
        var ctxFull = Path.GetFullPath(Path.Combine(composeDir, (b.Context ?? ".").Replace('/', Path.DirectorySeparatorChar)));
        var dockerfile = (b.Dockerfile ?? "Dockerfile").Replace('\\', '/');
        var dfFull = Path.IsPathRooted(dockerfile) ? dockerfile : Path.GetFullPath(Path.Combine(ctxFull, dockerfile.Replace('/', Path.DirectorySeparatorChar)));
        var found = all.FirstOrDefault(f => SamePath(f.Full, dfFull));
        if (found is null) warnings.Add($"The compose file builds '{cs.Name}' with {dockerfile}, but that file was not found.");
        else claimed.Add(found.Full);

        var info = found?.Info;
        return Make(root, cs.Name, dockerfile, ctxFull, b.Target, b.Args, cs, info, Confidence.High, [],
            [$"The compose file builds it from {Rel(root, ctxFull)} with {dockerfile}."]);
    }

    // ── services matched to Dockerfiles by name ──────────────────────────────

    private static AdoptedService? FromNames(string root, ComposeServiceInfo cs, List<Found> all, HashSet<string> claimed)
    {
        var svc = Norm(cs.Name);
        var repo = Norm(LastSegment(ImageRepo(cs.Image)));
        var scored = all.Where(f => !claimed.Contains(f.Full))
            .Select(f => (File: f, Score: Score(f, svc, repo)))
            .Where(x => x.Score >= 50).OrderByDescending(x => x.Score).ThenBy(x => x.File.Rel, StringComparer.OrdinalIgnoreCase).ToList();
        if (scored.Count == 0) return null;

        var top = scored[0].Score;
        var tied = scored.Where(x => x.Score == top).ToList();
        if (tied.Count > 1)
        {
            var alts = tied.Select(x => x.File.Rel).ToList();
            return new AdoptedService(cs.Name, null, ".", null, new Dictionary<string, string>(), ImageName(cs.Image), cs.Name, Confidence.Low,
                alts, [], [], StyleOf(cs).Style, StyleOf(cs).Variable,
                [$"Pick the right one: several Dockerfiles could build '{cs.Name}' equally well."]);
        }

        var best = scored[0].File;
        claimed.Add(best.Full);
        var alternatives = scored.Skip(1).Select(x => x.File.Rel).ToList();
        var ctx = InferContext(root, best);
        var conf = top >= 90 ? Confidence.High : top >= 60 ? Confidence.Medium : Confidence.Low;
        return Make(root, cs.Name, DockerfileRelative(ctx, best.Full), ctx, null, new Dictionary<string, string>(), cs, best.Info, conf, alternatives,
            [$"Its name matches {best.Rel}."]);
    }

    private static AdoptedService FromDockerfile(string root, Found f, string name)
    {
        var ctx = InferContext(root, f);
        return new AdoptedService(name, DockerfileRelative(ctx, f.Full), RelativeContext(root, ctx), null, new Dictionary<string, string>(), null, null,
            Confidence.Medium, [], f.Info.OtherEnds, NeedingValue(f.Info, new Dictionary<string, string>()), TagStyle.None, null,
            [$"Found {f.Rel}; there is no compose file, so it is named from its file or folder."]);
    }

    private static AdoptedService Make(string root, string name, string dockerfile, string ctxFull, string? target,
        IReadOnlyDictionary<string, string> args, ComposeServiceInfo cs, DockerfileInfo? info, Confidence conf,
        IReadOnlyList<string> alternatives, IReadOnlyList<string> reasons)
    {
        var (style, variable) = StyleOf(cs);
        return new AdoptedService(name, dockerfile, RelativeContext(root, ctxFull), target, args, ImageName(cs.Image), cs.Name, conf, alternatives,
            info?.OtherEnds ?? [], info is null ? [] : NeedingValue(info, args), style, variable, reasons);
    }

    // ── scoring ──────────────────────────────────────────────────────────────

    private static int Score(Found f, string svc, string repo)
    {
        var stem = f.Stem is null ? null : Norm(f.Stem);
        var folder = f.Folder is null ? null : Norm(f.Folder);
        var s = 0;
        if (stem is not null && stem == svc) s = 100;
        else if (stem is null && folder == svc) s = 90;
        else if (stem is not null && folder == svc) s = 80;
        else if (stem is { Length: >= 3 } && repo.Length > 0 && repo.EndsWith(stem, StringComparison.Ordinal)) s = 70;
        else if (folder is { Length: >= 3 } && repo.Length > 0 && repo.EndsWith(folder, StringComparison.Ordinal)) s = 60;
        else if (stem is { Length: >= 3 } && svc.Length >= 3 && (svc.Contains(stem, StringComparison.Ordinal) || stem.Contains(svc, StringComparison.Ordinal))) s = 50;
        return s == 0 ? 0 : s - f.Depth * 5;
    }

    // ── the build context ────────────────────────────────────────────────────

    /// <summary>
    /// The nearest folder (from the Dockerfile's own folder upward) where everything the Dockerfile copies exists.
    /// A Dockerfile that copies nothing from the context, or nothing that can be found, builds from its own folder.
    /// </summary>
    internal static string InferContext(string root, Found f)
    {
        var dir = Path.GetDirectoryName(f.Full)!;
        var sources = f.Info.CopySources.Where(s => s is not "." and not "./").ToList();
        if (sources.Count == 0) return dir;
        for (var d = dir; d is not null && IsSameOrBelow(root, d); d = Path.GetDirectoryName(d))
        {
            if (sources.All(s => SourceExists(d, s))) return d;
            if (SamePath(d, root)) break;
        }
        return dir;
    }

    private static bool SourceExists(string dir, string source)
    {
        var parts = source.Replace('\\', '/').TrimStart('/').Split('/', StringSplitOptions.RemoveEmptyEntries).Where(p => p != ".").ToList();
        if (parts.Count == 0) return true;
        var current = dir;
        foreach (var part in parts)
        {
            if (part.IndexOfAny(['*', '?', '[']) >= 0)
                return Directory.Exists(current) && Directory.EnumerateFileSystemEntries(current, part).Any();
            var next = Path.Combine(current, part);
            if (!File.Exists(next) && !Directory.Exists(next)) return false;
            current = next;
        }
        return true;
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static IEnumerable<string> Walk(string root, string dir, int depth)
    {
        if (depth > MaxDepth) yield break;
        IEnumerable<string> files, dirs;
        try { files = Directory.GetFiles(dir); dirs = Directory.GetDirectories(dir); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }
        foreach (var f in files) yield return f;
        foreach (var d in dirs)
            if (!SkippedFolders.Contains(Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
                foreach (var f in Walk(root, d, depth + 1)) yield return f;
    }

    private static bool IsDockerfile(string path)
    {
        var n = Path.GetFileName(path);
        if (n.EndsWith(".dockerignore", StringComparison.OrdinalIgnoreCase)) return false;
        return n.Equals("dockerfile", StringComparison.OrdinalIgnoreCase)
            || n.StartsWith("dockerfile.", StringComparison.OrdinalIgnoreCase)
            || n.EndsWith(".dockerfile", StringComparison.OrdinalIgnoreCase);
    }

    private static Found Read(string root, string path)
    {
        var name = Path.GetFileName(path);
        string? stem = null;
        if (name.EndsWith(".dockerfile", StringComparison.OrdinalIgnoreCase)) stem = name[..^".dockerfile".Length];
        else if (name.StartsWith("dockerfile.", StringComparison.OrdinalIgnoreCase)) stem = name["dockerfile.".Length..];
        var parent = Path.GetDirectoryName(path)!;
        var folder = SamePath(parent, root) ? null : Path.GetFileName(parent);
        string text;
        try { text = File.ReadAllText(path); } catch (IOException) { text = ""; }
        return new Found(path, Rel(root, path), Depth(root, path), stem, folder, DockerfileParser.Parse(text));
    }

    private static string NameFor(string root, Found f)
    {
        var raw = f.Stem ?? f.Folder ?? Path.GetFileName(root);
        var slug = Regex.Replace(raw.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return slug.Length > 0 ? slug : "app";
    }

    private static string Unique(string name, HashSet<string> used)
    {
        var n = name;
        for (var i = 2; !used.Add(n); i++) n = $"{name}-{i}";
        return n;
    }

    private static IReadOnlyList<string> NeedingValue(DockerfileInfo info, IReadOnlyDictionary<string, string> given) =>
        info.Args.Where(a => !a.HasDefault && !given.ContainsKey(a.Name)).Select(a => a.Name).Distinct().ToList();

    private static string DockerfileRelative(string contextFull, string dockerfileFull) =>
        Path.GetRelativePath(contextFull, dockerfileFull).Replace('\\', '/');

    private static string RelativeContext(string root, string ctxFull)
    {
        var rel = Path.GetRelativePath(root, ctxFull).Replace('\\', '/');
        return rel is "" ? "." : rel;
    }

    private static string Rel(string root, string full) => Path.GetRelativePath(root, full).Replace('\\', '/');

    private static int Depth(string root, string full) => Rel(root, full).Count(c => c == '/');

    private static bool SamePath(string a, string b) =>
        string.Equals(a.TrimEnd('/', '\\'), b.TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase);

    private static bool IsSameOrBelow(string root, string dir) =>
        Path.GetRelativePath(root, dir) is var r && !r.StartsWith("..") && !Path.IsPathRooted(r);

    private static string Norm(string s) => Regex.Replace(s.ToLowerInvariant(), "[^a-z0-9]", "");

    // ── images and tags ──────────────────────────────────────────────────────

    /// <summary>The image without its tag or tag variable: <c>reg/repo:${IMAGE_TAG:-latest}</c> becomes <c>reg/repo</c>.</summary>
    internal static string? ImageName(string? image)
    {
        if (string.IsNullOrWhiteSpace(image)) return null;
        var i = image.Trim();
        if (i.StartsWith("${")) return null;
        var v = i.IndexOf(":${", StringComparison.Ordinal);
        if (v >= 0) return i[..v];
        var colon = i.LastIndexOf(':');
        return colon > i.LastIndexOf('/') ? i[..colon] : i;
    }

    private static string ImageRepo(string? image) => ImageName(image) ?? "";

    private static string LastSegment(string repo) => repo.Split('/').LastOrDefault() ?? "";

    private static (TagStyle Style, string? Variable) StyleOf(ComposeServiceInfo cs)
    {
        var image = cs.Image?.Trim();
        if (string.IsNullOrEmpty(image)) return (TagStyle.None, null);
        if (Regex.Match(image, @"^\$\{(?<v>[A-Za-z_][A-Za-z0-9_]*)(:?-[^}]*)?\}$") is { Success: true } whole)
            return (TagStyle.PerServiceVariable, whole.Groups["v"].Value);
        if (Regex.Match(image, @":\$\{(?<v>[A-Za-z_][A-Za-z0-9_]*)(:?-[^}]*)?\}$") is { Success: true } tail)
            return (TagStyle.SharedVariable, tail.Groups["v"].Value);
        var colon = image.LastIndexOf(':');
        return colon > image.LastIndexOf('/') ? (TagStyle.Literal, null) : (TagStyle.None, null);
    }
}
