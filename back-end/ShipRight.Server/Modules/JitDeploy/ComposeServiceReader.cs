using System.Text.RegularExpressions;
using ShipRight.Modules.DockerAdoption;
using ShipRight.Modules.Projects;
using ShipRight.Modules.Setup;

namespace ShipRight.Modules.JitDeploy;

/// <summary>A compose file that cannot be used, with a plain-language reason.</summary>
public sealed class ComposeFileException(string message) : Exception(message);

/// <param name="Built">It has its own build section in the compose file.</param>
/// <param name="Note">What to change in the compose file for this service, or null when it already works.</param>
public sealed record ComposeServiceCandidate(string Name, string ImageRef, string TagVariable, bool Built, bool Suggested, string? Note);

public sealed record ComposeFileReading(
    string RelativePath, string RepoRoot, IReadOnlyList<ComposeServiceCandidate> Services, IReadOnlyList<string> LeftAlone, IReadOnlyList<string> Notes);

/// <summary>
/// Reads a compose file the person picked (on this computer) and works out, for build-on-server, which services ShipRight should build
/// (those with a build section, or a Dockerfile that matches them) and where the file sits inside its git repository. It only reads.
/// </summary>
public static class ComposeServiceReader
{
    private const long MaxBytes = 1_000_000;
    private static readonly Regex WholeVariable = new(@"^\$\{(?<v>[A-Za-z_][A-Za-z0-9_]*):?-(?<d>[^}]*)\}$", RegexOptions.Compiled);

    public static ComposeFileReading Read(string localFilePath, string projectName)
    {
        if (string.IsNullOrWhiteSpace(localFilePath) || !File.Exists(localFilePath))
            throw new ComposeFileException("That file was not found.");
        var full = Path.GetFullPath(localFilePath);
        if (!full.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) && !full.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
            throw new ComposeFileException("Pick a compose file: its name ends in .yml or .yaml.");
        if (new FileInfo(full).Length > MaxBytes)
            throw new ComposeFileException("That file is too large to be a compose file.");

        var repoRoot = FindGitRoot(Path.GetDirectoryName(full)!)
            ?? throw new ComposeFileException("That file is not inside a git repository, so the server could not download it. Put your compose file inside the repository.");
        var rel = Path.GetRelativePath(repoRoot, full).Replace('\\', '/');
        try { JitNames.RepoFile(rel); }
        catch (ArgumentException)
        {
            throw new ComposeFileException("The path of that file has characters ShipRight cannot use (spaces or special characters). Rename the folder or the file, then pick it again.");
        }

        var services = ComposeLite.Parse(File.ReadAllText(full));
        AdoptionPlan? plan = null;
        try { plan = DockerSetupAnalyzer.Analyse(repoRoot); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* the Dockerfiles just cannot help */ }
        var matches = plan is not null && string.Equals(plan.ComposeFile, rel, StringComparison.OrdinalIgnoreCase)
            ? plan.Services.Where(s => s.ComposeService is not null && s.DockerfilePath is not null).ToDictionary(s => s.ComposeService!, StringComparer.Ordinal)
            : new Dictionary<string, AdoptedService>();

        var candidates = new List<ComposeServiceCandidate>();
        var leftAlone = new List<string>();
        foreach (var cs in services)
        {
            var built = cs.Build is not null;
            matches.TryGetValue(cs.Name, out var matched);
            if (!built && matched is null) { leftAlone.Add(cs.Name); continue; }

            var variable = PerServiceVariable(cs.Image) ?? GeneratedCompose.TagVariable(cs.Name);
            var image = ImageRefOf(cs.Image) ?? matched?.ImageName ?? $"{Slug(projectName)}/{Slug(cs.Name)}";
            var note = PerServiceVariable(cs.Image) is null
                ? $"Its image line needs to read image: ${{{variable}:-{image}:latest}} so each release gets its own tag. \"Adopt Docker setup\" can add that for you."
                : null;
            candidates.Add(new ComposeServiceCandidate(cs.Name, image, variable, built, true, note));
        }

        var notes = candidates.Count == 0
            ? new List<string> { "There is nothing to build in this file: none of its services has a build section or a matching Dockerfile." }
            : [];
        return new ComposeFileReading(rel, repoRoot, candidates, leftAlone, notes);
    }

    private static string? PerServiceVariable(string? image) =>
        image is not null && WholeVariable.Match(image.Trim()) is { Success: true } m ? m.Groups["v"].Value : null;

    /// <summary>The image without its tag, also when it is written inside its own tag variable.</summary>
    private static string? ImageRefOf(string? image)
    {
        if (string.IsNullOrWhiteSpace(image)) return null;
        var m = WholeVariable.Match(image.Trim());
        return DockerSetupAnalyzer.ImageName(m.Success ? m.Groups["d"].Value : image);
    }

    private static string Slug(string v)
    {
        var s = Regex.Replace((v ?? "").ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return s.Length > 0 ? s : "app";
    }

    private static string? FindGitRoot(string start)
    {
        for (var d = start; d is not null; d = Path.GetDirectoryName(d))
            if (Directory.Exists(Path.Combine(d, ".git")) || File.Exists(Path.Combine(d, ".git"))) return d;
        return null;
    }
}

public sealed record ComposeFileRequest(string? Path);

/// <summary>POST /api/jit/{id}/compose-file: reads the compose file picked in the card. Read-only.</summary>
public static class JitComposeFileRoutes
{
    public static void MapJitComposeFileRoutes(this WebApplication app) =>
        app.MapPost("/api/jit/{id}/compose-file", (string id, ComposeFileRequest req, IProjectStore projects) => HandleAsync(id, req, projects));

    internal static async Task<IResult> HandleAsync(string id, ComposeFileRequest req, IProjectStore projects)
    {
        var project = await projects.GetByIdAsync(id);
        if (project is null) return Results.NotFound(ProjectRouter.Error($"Project '{id}' not found.", null));
        if (string.IsNullOrWhiteSpace(req.Path)) return Results.BadRequest(ProjectRouter.Error("Pick a compose file first.", "path"));
        try
        {
            var r = ComposeServiceReader.Read(req.Path, project.Name);
            return Results.Ok(new { relativePath = r.RelativePath, repoRoot = r.RepoRoot, services = r.Services, leftAlone = r.LeftAlone, notes = r.Notes });
        }
        catch (ComposeFileException ex)
        {
            return Results.BadRequest(ProjectRouter.Error(ex.Message, "path"));
        }
    }
}

public sealed record ServiceHelp(string Explanation, IReadOnlyList<string> Steps, ComposeFileReading? Found);

/// <summary>
/// "This project has no services to deploy" with the reason and the fix, using the compose file ShipRight can find in the project's
/// own folder. The original wording of the problem is never changed; the help is added beside it.
/// </summary>
public static class JitServiceHelp
{
    public const string NoServices = "This project has no services to deploy.";

    private static readonly string[] DefaultFiles = ["docker-compose.yml", "docker-compose.yaml", "compose.yml", "compose.yaml"];

    public static ServiceHelp? For(ProjectConfig project, JitDeployConfig cfg, IReadOnlyList<string> problems)
    {
        if (!problems.Contains(NoServices)) return null;

        const string why = "Building on the server needs to know which parts of your compose file to build and tag. ShipRight normally takes that list from the project's own services, and this project has none.";
        var named = (cfg.ComposeFile ?? "").Trim();
        ComposeFileReading? found = null;

        foreach (var repo in project.GitRepos.Select(g => g.RepoPath).Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            string[] candidates = named.Length > 0 ? [named] : DefaultFiles;
            foreach (var file in candidates)
            {
                try
                {
                    if (!Directory.Exists(repo)) break;
                    var path = Path.Combine(repo, file.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(path)) continue;
                    found = ComposeServiceReader.Read(path, project.Name);
                    break;
                }
                catch (Exception ex) when (ex is ComposeFileException or IOException or UnauthorizedAccessException) { /* try the next */ }
            }
            if (found is not null) break;
        }

        var steps = new List<string>();
        if (found is { Services.Count: > 0 })
        {
            var first = found.Services[0];
            steps.Add($"ShipRight read {found.RelativePath} in your project folder and found these services it can build: {string.Join(", ", found.Services.Select(s => s.Name))}. Press \"Use these services\" below, then Save settings.");
            steps.Add($"Each service's image line in the compose file should look like image: ${{VAR:-repo:latest}} (for example {first.TagVariable} for {first.Name}). The panel \"What your compose file needs\" shows the exact lines, and \"Adopt Docker setup\" can add them for you.");
        }
        else if (found is not null)
        {
            steps.Add($"ShipRight read {found.RelativePath} but none of its services has a build: section or a matching Dockerfile, so there is nothing to build.");
            steps.Add("Add a build: section to each service you want built (for example build: ./api), or make sure a Dockerfile named after the service exists. \"Adopt Docker setup\" can work this out from your Dockerfiles.");
        }
        else
        {
            if (named.Length > 0) steps.Add($"ShipRight could not find {named} in your project folder.");
            steps.Add("Pick your compose file with Browse next to \"Compose file in your repository\". ShipRight finds the services in it for you.");
            steps.Add("If you do not have one yet, put a docker-compose.yml in the top folder of your repository (that is where ShipRight looks first) and describe each service you want built with a build: section.");
            steps.Add("Or add the services to the project itself (Edit project, then its services) if you prefer ShipRight to manage them.");
        }
        return new ServiceHelp(why, steps, found);
    }

    /// <summary>What a deploy that cannot start says: the problems as they were, plus the fix when the problem is the missing services.</summary>
    public static string StartMessage(IReadOnlyList<string> problems)
    {
        var text = string.Join(" ", problems);
        return problems.Contains(NoServices)
            ? text + " Build on the server needs to know which parts of your compose file to build: open Build on the server on the project's Overview page, pick your compose file with Browse and use the services it finds."
            : text;
    }
}
