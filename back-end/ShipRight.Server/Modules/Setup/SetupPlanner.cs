namespace ShipRight.Modules.Setup;

/// <summary>
/// Turns "a folder the user picked" into a reviewable plan: what was recognised, what already exists,
/// and exactly which files ShipRight would create. Planning is read-only.
/// </summary>
public sealed class SetupPlanner
{
    private const string FirstVersion = "0.1.0";

    private static readonly string[] CommonGitIgnore =
        [".env", ".env.*", "!.env.example", "*.log", ".DS_Store", "Thumbs.db"];

    private static readonly string[] EnvIgnorePatterns = [".env", ".env*", ".env.*", "*.env"];

    private readonly IReadOnlyList<IStackProvider> _providers;

    public SetupPlanner(IEnumerable<IStackProvider> providers) => _providers = providers.ToList();

    public static SetupPlanner CreateDefault() => new([new DotNetStackProvider(), new NodeStackProvider()]);

    public SetupPlan Plan(string rootPath)
    {
        var root = TrimEnd(Path.GetFullPath(rootPath));
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"Folder not found: {rootPath}");

        var warnings = new List<string>();
        var found = new List<(IStackProvider Provider, DetectedComponent Component)>();
        foreach (var provider in _providers)
        {
            var detection = provider.Detect(root);
            warnings.AddRange(detection.Warnings);
            found.AddRange(detection.Components.Select(c => (provider, c)));
        }
        found = found.OrderBy(f => f.Component.Directory, StringComparer.OrdinalIgnoreCase).ToList();

        var components = found.Select(f => f.Component).ToList();
        if (components.Count == 0)
        {
            warnings.Add("ShipRight did not recognise a .NET or Node.js project in this folder. Pick the folder that contains your project files.");
            return new SetupPlan(root, root, components, [], warnings);
        }

        var existingRepo = FindGitRoot(root);
        var repoRoot = existingRepo ?? root;
        if (existingRepo is not null && !SamePath(existingRepo, root))
            warnings.Add($"Your project folder is inside a larger git repository at {existingRepo}. ShipRight will use that repository.");

        var items = new List<PlanItem>();
        AddGit(items, repoRoot, existingRepo is not null);
        AddGitIgnore(items, warnings, repoRoot, found.Select(f => f.Provider).Distinct().ToList());
        AddGitAttributes(items, repoRoot);

        var claimedContexts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (provider, component) in found)
        {
            foreach (var note in component.Notes) warnings.Add($"{component.Name}: {note}");
            AddComponentFiles(items, warnings, repoRoot, provider, component, claimedContexts);
        }

        return new SetupPlan(root, repoRoot, components, items, warnings,
            existingRepo is null ? NewRepoBranch : ReadBranch(repoRoot));
    }

    private const string NewRepoBranch = "main";

    /// <summary>The branch named in .git/HEAD; null for a detached HEAD or a .git file (worktrees, submodules).</summary>
    private static string? ReadBranch(string repoRoot)
    {
        try
        {
            var head = Path.Combine(repoRoot, ".git", "HEAD");
            if (!File.Exists(head)) return null;
            const string prefix = "ref: refs/heads/";
            var line = File.ReadAllText(head).Trim();
            return line.StartsWith(prefix, StringComparison.Ordinal) && line.Length > prefix.Length
                ? line[prefix.Length..]
                : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static void AddGit(List<PlanItem> items, string repoRoot, bool exists) =>
        items.Add(Item(repoRoot, PlanItemKind.GitRepo, Path.Combine(repoRoot, ".git"),
            exists ? PlanItemStatus.Exists : PlanItemStatus.WillCreate,
            exists ? "Your project is already tracked by git."
                   : "Sets up git so ShipRight can build exact, saved versions of your code.",
            content: null, componentName: null));

    private void AddGitIgnore(List<PlanItem> items, List<string> warnings, string repoRoot, List<IStackProvider> providers)
    {
        var path = Path.Combine(repoRoot, ".gitignore");
        if (File.Exists(path))
        {
            if (!CoversEnvFiles(File.ReadAllLines(path)))
                warnings.Add("Your .gitignore does not ignore .env files, so secrets could be saved to git by accident. Add a line containing .env to it.");
            items.Add(Item(repoRoot, PlanItemKind.GitIgnore, path, PlanItemStatus.Exists,
                "You already have a .gitignore, so ShipRight leaves it alone.", null, null));
            return;
        }

        var lines = new List<string> { "# Created by ShipRight - edit freely.", "# Secrets and noise" };
        lines.AddRange(CommonGitIgnore);
        foreach (var provider in providers)
        {
            lines.Add("");
            lines.Add($"# {provider.DisplayName}");
            lines.AddRange(provider.GitIgnoreEntries);
        }
        items.Add(Item(repoRoot, PlanItemKind.GitIgnore, path, PlanItemStatus.WillCreate,
            "Stops build output and secret .env files from being saved to git by accident.",
            StackFiles.Lines(lines.ToArray()), null));
    }

    private static void AddGitAttributes(List<PlanItem> items, string repoRoot)
    {
        var path = Path.Combine(repoRoot, ".gitattributes");
        var exists = File.Exists(path);
        items.Add(Item(repoRoot, PlanItemKind.GitAttributes, path,
            exists ? PlanItemStatus.Exists : PlanItemStatus.WillCreate,
            exists ? "You already have a .gitattributes, so ShipRight leaves it alone."
                   : "Keeps line endings consistent so scripts and Dockerfiles work the same on Windows and Linux.",
            exists ? null : StackFiles.Lines(
                "# Created by ShipRight - edit freely.",
                "* text=auto",
                "*.sh text eol=lf",
                "Dockerfile* text eol=lf",
                "*.yml text eol=lf"),
            null));
    }

    private static void AddComponentFiles(
        List<PlanItem> items, List<string> warnings, string repoRoot,
        IStackProvider provider, DetectedComponent c, Dictionary<string, string> claimedContexts)
    {
        var ctx = c.ContextDirectory;
        var existingDockerfile = StackFiles.DockerfileIn(ctx);

        if (existingDockerfile is not null)
        {
            // A shared, existing Dockerfile may serve several components; nothing to plan or warn about.
            claimedContexts.TryAdd(ctx, c.Name);
            items.Add(Item(repoRoot, PlanItemKind.Dockerfile, existingDockerfile, PlanItemStatus.Exists,
                $"{c.Name} already has a Dockerfile, so ShipRight leaves it alone.", null, c.Name));
        }
        else if (claimedContexts.TryGetValue(ctx, out var owner))
        {
            warnings.Add($"{c.Name}: shares its folder with {owner}, so it cannot get its own Dockerfile. Add one yourself or move it into its own folder.");
            return;
        }
        else
        {
            claimedContexts[ctx] = c.Name;
            items.Add(Item(repoRoot, PlanItemKind.Dockerfile, Path.Combine(ctx, "Dockerfile"), PlanItemStatus.WillCreate,
                $"Tells Docker how to build and run {c.Name}. ShipRight wrote this for a {provider.DisplayName} project and you can edit it any time.",
                provider.RenderDockerfile(c), c.Name));
        }

        var ignorePath = Path.Combine(ctx, ".dockerignore");
        var ignoreExists = File.Exists(ignorePath);
        items.Add(Item(repoRoot, PlanItemKind.DockerIgnore, ignorePath,
            ignoreExists ? PlanItemStatus.Exists : PlanItemStatus.WillCreate,
            ignoreExists ? $"{c.Name} already has a .dockerignore, so ShipRight leaves it alone."
                         : $"Keeps build leftovers, git history and secrets out of {c.Name}'s image.",
            ignoreExists ? null : provider.RenderDockerIgnore(c), c.Name));

        var versionPath = new[] { Path.Combine(ctx, "version.txt"), Path.Combine(c.Directory, "version.txt") }
            .FirstOrDefault(File.Exists);
        items.Add(Item(repoRoot, PlanItemKind.VersionFile, versionPath ?? Path.Combine(ctx, "version.txt"),
            versionPath is not null ? PlanItemStatus.Exists : PlanItemStatus.WillCreate,
            versionPath is not null ? $"{c.Name} already has a version.txt, so ShipRight leaves it alone."
                                    : $"ShipRight reads and bumps the version of {c.Name} from this file.",
            versionPath is not null ? null : (c.Version ?? FirstVersion) + "\n", c.Name));
    }

    private static PlanItem Item(
        string repoRoot, PlanItemKind kind, string path, PlanItemStatus status,
        string reason, string? content, string? componentName)
    {
        var rel = StackFiles.RelativeTo(repoRoot, path);
        return new PlanItem($"{kind}:{rel}", kind, status, path, rel, reason, content, componentName);
    }

    private static bool CoversEnvFiles(IEnumerable<string> gitIgnoreLines) =>
        gitIgnoreLines
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#') && !l.StartsWith('!'))
            .Select(l => l.TrimStart('/'))
            .Select(l => l.StartsWith("**/") ? l[3..] : l)
            .Any(l => EnvIgnorePatterns.Contains(l));

    private static string? FindGitRoot(string start)
    {
        var current = start;
        while (current is not null)
        {
            var git = Path.Combine(current, ".git");
            if (Directory.Exists(git) || File.Exists(git)) return current;
            current = Path.GetDirectoryName(current);
        }
        return null;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(TrimEnd(a), TrimEnd(b), StringComparison.OrdinalIgnoreCase);

    private static string TrimEnd(string path) =>
        path.Length > 3 ? path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : path;
}
