using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ShipRight.Modules.DockerAdoption;
using ShipRight.Modules.Projects;

namespace ShipRight.Modules.JitDeploy;

/// <summary>How one service is built, with every path relative to the root of the repository ("." is the root itself).</summary>
public sealed record JitBuildRecipe(string Context, string Dockerfile, string? Target = null, IReadOnlyDictionary<string, string>? Args = null);

/// <summary>
/// The small file that hands each service's build instructions (Dockerfile, build folder, stage, arguments) and its image name and
/// release tag to compose, so the person's own compose file needs no changes. It is generated from the settings before every deploy
/// and lives beside the deploy state on the server, never in the repository (a repository clean-up would delete it). Every value is
/// checked and every string is written as a quoted, escaped scalar, so nothing in a setting can change its meaning.
/// </summary>
public static class ComposeOverride
{
    private static readonly Regex PathShape = new(@"^[A-Za-z0-9._][A-Za-z0-9._/-]*$", RegexOptions.Compiled);
    private static readonly Regex TargetShape = new(@"^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.Compiled);
    private static readonly Regex ArgName = new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    public static string Path(JitTarget t) => t.StateDir + "/compose.override.yml";

    /// <summary>The file's text, or null when no service has a recipe.</summary>
    public static string? Render(string composeFile, IReadOnlyList<JitService> services)
    {
        var withRecipe = services.Where(s => s.Recipe is not null).ToList();
        if (withRecipe.Count == 0) return null;

        var composeDir = DirOf(JitNames.RepoFile(composeFile));
        var sb = new StringBuilder();
        sb.Append("# Build instructions and image tags for a build-on-server deploy.\n");
        sb.Append("# Written before every deploy from the settings; changes made here are overwritten.\n");
        sb.Append("services:\n");

        foreach (var s in withRecipe)
        {
            var r = s.Recipe!;
            var context = RepoPath(r.Context, "build folder", allowRoot: true);
            var dockerfile = RepoPath(r.Dockerfile, "Dockerfile", allowRoot: false);
            var target = string.IsNullOrWhiteSpace(r.Target) ? null : r.Target.Trim();
            if (target is not null && !TargetShape.IsMatch(target)) throw new ArgumentException("The build stage name is not valid. Use the name of a stage in the Dockerfile.");

            sb.Append($"  {Q(JitNames.Service(s.Name))}:\n");
            sb.Append("    build:\n");
            sb.Append($"      context: {Q(RelativeFrom(composeDir, context))}\n");
            sb.Append($"      dockerfile: {Q(RelativeFrom(context == "." ? "" : context, dockerfile))}\n");
            if (target is not null) sb.Append($"      target: {Q(target)}\n");
            if (r.Args is { Count: > 0 })
            {
                sb.Append("      args:\n");
                foreach (var (k, v) in r.Args.OrderBy(x => x.Key, StringComparer.Ordinal))
                {
                    if (!ArgName.IsMatch(k)) throw new ArgumentException("A build argument name is not valid. Use letters, digits and _ only, not starting with a digit.");
                    if ((v ?? "").Any(char.IsControl)) throw new ArgumentException($"The value of build argument '{k}' contains a line break or another character that is not allowed.");
                    sb.Append($"        {Q(k)}: {Q(v ?? "")}\n");
                }
            }
            sb.Append($"    image: {Q($"${{{JitNames.TagVariable(s.TagVariable)}:-{JitNames.ImageRef(s.ImageRef)}:latest}}")}\n");
        }
        return sb.ToString();
    }

    /// <summary>Plain-language reason a recipe cannot be used, or null. Never repeats the values.</summary>
    public static string? Problem(string serviceName, JitBuildRecipe recipe)
    {
        try { Render("docker-compose.yml", [new JitService(serviceName, "X_TAG", "x/x", recipe)]); return null; }
        catch (ArgumentException ex) { return ex.Message; }
    }

    public static string WriteCommand(JitTarget t, string yaml)
    {
        JitNames.Path(t.ProjectDir);
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(yaml));
        var file = ShellQuote.Single(Path(t));
        var tmp = ShellQuote.Single(Path(t) + ".tmp");
        return $"mkdir -p {ShellQuote.Single(t.StateDir)} && umask 077 && echo '{b64}' | base64 -d > {tmp} && mv -f {tmp} {file}";
    }

    private static string Q(string value) => JsonSerializer.Serialize(value);

    private static string RepoPath(string value, string what, bool allowRoot)
    {
        var v = (value ?? "").Trim();
        if (allowRoot && v == ".") return ".";
        var ok = PathShape.IsMatch(v) && !v.Split('/').Any(p => p.Length == 0 || p == "..");
        return ok ? v : throw new ArgumentException($"The {what} must be a path inside your repository, without spaces or special characters.");
    }

    private static string DirOf(string repoFile)
    {
        var i = repoFile.LastIndexOf('/');
        return i < 0 ? "" : repoFile[..i];
    }

    /// <summary>The path to <paramref name="to"/> as seen from <paramref name="from"/>; both relative to the repository root, "" being the root.</summary>
    internal static string RelativeFrom(string from, string to)
    {
        var f = from.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        var t = (to == "." ? "" : to).Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        var common = 0;
        while (common < f.Count && common < t.Count && f[common] == t[common]) common++;
        var parts = Enumerable.Repeat("..", f.Count - common).Concat(t.Skip(common)).ToList();
        return parts.Count == 0 ? "." : string.Join('/', parts);
    }
}

public sealed record DockerfileDescription(
    string RepoRoot, string DockerfileRelative, string ContextRelative, string DockerfileInContext,
    IReadOnlyList<string> OtherStages, IReadOnlyList<string> ArgsNeedingAValue);

/// <summary>Works out, for a Dockerfile the person picked on this computer, where it is in the repository, which folder it builds from, and what it needs.</summary>
public static class DockerfileDescriber
{
    private const long MaxBytes = 200_000;

    public static DockerfileDescription Describe(string localFilePath)
    {
        if (string.IsNullOrWhiteSpace(localFilePath) || !File.Exists(localFilePath)) throw new ComposeFileException("That file was not found.");
        var full = Path.GetFullPath(localFilePath);
        if (new FileInfo(full).Length > MaxBytes) throw new ComposeFileException("That file is too large to be a Dockerfile.");

        var root = FindGitRoot(Path.GetDirectoryName(full)!)
                   ?? throw new ComposeFileException("That file is not inside a git repository, so the server could not download it. Pick a Dockerfile inside the repository.");
        var rel = Path.GetRelativePath(root, full).Replace('\\', '/');
        if (!Regex.IsMatch(rel, @"^[A-Za-z0-9._][A-Za-z0-9._/-]*$") || rel.Split('/').Any(p => p.Length == 0 || p == ".."))
            throw new ComposeFileException("The path of that file has characters ShipRight cannot use (spaces or special characters). Rename the folder or the file, then pick it again.");

        var found = DockerSetupAnalyzer.Read(root, full);
        var context = DockerSetupAnalyzer.InferContext(root, found);
        return new DockerfileDescription(
            root, rel, DockerSetupAnalyzer.RelativeContext(root, context), DockerSetupAnalyzer.DockerfileRelative(context, full),
            found.Info.OtherEnds, DockerSetupAnalyzer.NeedingValue(found.Info, new Dictionary<string, string>()));
    }

    private static string? FindGitRoot(string start)
    {
        for (var d = start; d is not null; d = Path.GetDirectoryName(d))
            if (Directory.Exists(Path.Combine(d, ".git")) || File.Exists(Path.Combine(d, ".git"))) return d;
        return null;
    }
}

public sealed record DockerfileRequest(string? Path);

/// <summary>POST /api/jit/{id}/dockerfile: describes the Dockerfile picked for a service. Read-only.</summary>
public static class JitDockerfileRoutes
{
    public static void MapJitDockerfileRoutes(this WebApplication app) =>
        app.MapPost("/api/jit/{id}/dockerfile", (string id, DockerfileRequest req, IProjectStore projects) => HandleAsync(id, req, projects));

    internal static async Task<IResult> HandleAsync(string id, DockerfileRequest req, IProjectStore projects)
    {
        if (await projects.GetByIdAsync(id) is null) return Results.NotFound(ProjectRouter.Error($"Project '{id}' not found.", null));
        if (string.IsNullOrWhiteSpace(req.Path)) return Results.BadRequest(ProjectRouter.Error("Pick a Dockerfile first.", "path"));
        try
        {
            var d = DockerfileDescriber.Describe(req.Path);
            return Results.Ok(new
            {
                repoRoot = d.RepoRoot, dockerfileRelative = d.DockerfileRelative, contextRelative = d.ContextRelative,
                dockerfileInContext = d.DockerfileInContext, otherStages = d.OtherStages, argsNeedingAValue = d.ArgsNeedingAValue,
            });
        }
        catch (ComposeFileException ex)
        {
            return Results.BadRequest(ProjectRouter.Error(ex.Message, "path"));
        }
    }
}
