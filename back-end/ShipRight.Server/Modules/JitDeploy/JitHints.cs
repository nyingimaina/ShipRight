using System.Text.Json;
using System.Text.RegularExpressions;
using ShipRight.Modules.Projects;
using ShipRight.Shared.ProcessRunner;

namespace ShipRight.Modules.JitDeploy;

public sealed record RepoHint(string Id, string Label, string Url, string? Ssh, string? Https, string Source, string? Branch, bool HadCredentials);

public sealed record JitHints(IReadOnlyList<RepoHint> Repos, string? EnvFile, string? Branch, string ComposeProject, IReadOnlyList<string> Notes);

/// <summary>
/// What ShipRight already knows about a project that the build-on-server card should offer instead of asking: the git
/// addresses of its repositories (read from each repo's <c>origin</c>, never with a password or token), its deploy branch,
/// its env file, and the stack name it would run under. Reading is limited to <c>git remote get-url origin</c>.
/// </summary>
public sealed class JitHintsService(IProcessRunner runner)
{
    public async Task<JitHints> BuildAsync(ProjectConfig project, CancellationToken ct = default)
    {
        var notes = new List<string>();
        var repos = new List<RepoHint>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var git in project.GitRepos.Where(g => !string.IsNullOrWhiteSpace(g.RepoPath)))
        {
            var path = git.RepoPath.Trim();
            var origin = await TryReadOriginAsync(path, ct);
            if (origin is null)
            {
                notes.Add($"The repository at '{path}' has no git address (no 'origin' remote), so it cannot be offered.");
                continue;
            }
            var label = $"Source code ({FolderName(path)})";
            Add(repos, seen, notes, "project-repo", label, origin, string.IsNullOrWhiteSpace(git.DeployBranch) ? null : git.DeployBranch.Trim());
        }

        foreach (var url in new[] { project.Wsl.ComposeRepoUrl, project.Server.ComposeRepoUrl }.Where(u => !string.IsNullOrWhiteSpace(u)))
            Add(repos, seen, notes, "compose-repo", "Compose repository", url, null);

        if (repos.Count > 1)
            notes.Add("This project has more than one repository. Building on the server uses one repository that holds the compose file and the code of every service. Pick that one.");

        var branch = project.GitRepos.Select(g => g.DeployBranch).FirstOrDefault(b => !string.IsNullOrWhiteSpace(b))
                     ?? project.WatchBranch;
        var env = project.Server.EnvFile;

        return new JitHints(
            repos,
            string.IsNullOrWhiteSpace(env) ? null : env.Trim(),
            string.IsNullOrWhiteSpace(branch) ? null : branch.Trim(),
            JitDeployMapper.ComposeProjectName(project, new JitDeployConfig()),
            notes);
    }

    private static void Add(List<RepoHint> repos, HashSet<string> seen, List<string> notes, string source, string label, string rawUrl, string? branch)
    {
        var f = GitAddressForms.Analyse(rawUrl);
        if (f.Original.Length == 0 || !seen.Add(f.Key)) return;
        if (f.HadCredentials)
            notes.Add($"The saved address of '{label}' contained a password or token. ShipRight removed it from what it shows here. The server needs access another way, such as a deploy key.");
        repos.Add(new RepoHint($"{source}:{f.Key}", label, f.Original, f.Ssh, f.Https, source, branch, f.HadCredentials));
    }

    private async Task<string?> TryReadOriginAsync(string repoPath, CancellationToken ct)
    {
        try
        {
            var r = await runner.RunAsync("git", ["-C", repoPath, "remote", "get-url", "origin"], null, ct: ct, timeout: TimeSpan.FromSeconds(10));
            var url = r.StdOut.Trim();
            return r.Success && url.Length > 0 ? url : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }

    private static string FolderName(string path) =>
        path.TrimEnd('/', '\\').Split('/', '\\').LastOrDefault(s => s.Length > 0) ?? path;
}

public enum StackMatch { None, Same, Different }

public sealed record ComposeStack(string Name, string Status, IReadOnlyList<string> ConfigFiles);

public sealed record StackReport(IReadOnlyList<ComposeStack> Stacks, string ProjectName, StackMatch Match, IReadOnlyList<string> Warnings, string? SuggestedName = null);

/// <summary>
/// The stacks already running on the server, so taking one over is a choice and not an accident. Adopting a stack under a
/// different name would start a second copy of everything; adopting it from a different folder moves the place its
/// relative paths point at. Both are shown before the first deploy.
/// </summary>
public static class JitStacks
{
    public static string ListCommand() => "docker compose ls -a --format json";

    public static IReadOnlyList<ComposeStack> Parse(IEnumerable<string> lines)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.Join("\n", lines));
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
            var stacks = new List<ComposeStack>();
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                var name = e.TryGetProperty("Name", out var n) ? n.GetString() : null;
                if (string.IsNullOrWhiteSpace(name)) continue;
                var status = e.TryGetProperty("Status", out var s) ? s.GetString() ?? "" : "";
                var files = e.TryGetProperty("ConfigFiles", out var c) && c.GetString() is { } list
                    ? list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    : [];
                stacks.Add(new ComposeStack(name, status, files));
            }
            return stacks;
        }
        catch (JsonException) { return []; }
    }

    public static StackReport Evaluate(IReadOnlyList<ComposeStack> stacks, string projectName, string projectDir)
    {
        var dir = projectDir.TrimEnd('/');
        var repoDir = dir + "/repo";
        var warnings = new List<string>();
        var match = StackMatch.None;
        string? suggested = null;

        var same = stacks.FirstOrDefault(s => s.Name == projectName);
        if (same is not null)
        {
            match = StackMatch.Same;
            var fromRepo = same.ConfigFiles.Count > 0 && same.ConfigFiles.All(f => DirOf(f) == repoDir);
            if (!fromRepo)
            {
                var where = same.ConfigFiles.Count > 0 ? DirOf(same.ConfigFiles[0]) : "another folder";
                warnings.Add($"A stack named '{projectName}' is already running on this server, with its compose file in {where}. " +
                             $"Building on the server keeps the code in {repoDir}, so relative paths in your compose file (for example ./nginx/...) will point at the new folder, not the old one. " +
                             "Named volumes are kept. Check that nothing relies on files next to the old compose file.");
            }
        }
        else
        {
            var other = stacks.FirstOrDefault(s => s.ConfigFiles.Any(f => DirOf(f) == dir || DirOf(f) == repoDir));
            if (other is not null)
            {
                match = StackMatch.Different;
                suggested = other.Name;
                warnings.Add($"A stack named '{other.Name}' already runs from your project folder, but building on the server would use the name '{projectName}'. " +
                             $"That would start a second copy of everything next to it and could clash on ports. Pick '{other.Name}' as the stack name to take it over, or stop it first.");
            }
        }
        return new StackReport(stacks, projectName, match, warnings, suggested);
    }

    private static string DirOf(string file)
    {
        var i = file.LastIndexOf('/');
        return i <= 0 ? "/" : file[..i].TrimEnd('/');
    }
}

public sealed record BranchList(IReadOnlyList<string> Branches, string? DefaultBranch);

/// <summary>The branches of a repository as the SERVER sees them, so a deploy branch is chosen and not typed.</summary>
public static class JitBranches
{
    private static readonly Regex Head = new(@"^[0-9a-f]{40,64}\s+refs/heads/(?<b>\S+)$", RegexOptions.Compiled);
    private static readonly Regex Symref = new(@"^ref:\s+refs/heads/(?<b>\S+)\s+HEAD$", RegexOptions.Compiled);

    /// <summary>Refuses unsafe addresses and any address that carries a password or token. The failure message never repeats the address.</summary>
    public static string Command(string repoUrl)
    {
        var url = JitNames.RepoUrl(repoUrl?.Trim() ?? string.Empty);
        if (GitAddressForms.Analyse(url).HadCredentials)
            throw new ArgumentException("The address contains a password or token. Remove it and use a deploy key instead.");
        return $"GIT_TERMINAL_PROMPT=0 timeout 30 git ls-remote --symref -- {ShellQuote.Single(url)} HEAD 'refs/heads/*' 2>/dev/null || " +
               "{ echo '[branches] Cannot read the branches: the server cannot reach that repository yet. It may need a deploy key.'; exit 1; }";
    }

    public static BranchList Parse(IEnumerable<string> lines)
    {
        string? def = null;
        var names = new List<string>();
        foreach (var raw in lines)
        {
            var line = raw.Replace('\t', ' ').Trim();
            var sym = Symref.Match(line);
            if (sym.Success) { def = sym.Groups["b"].Value; continue; }
            var head = Head.Match(line);
            if (head.Success) names.Add(head.Groups["b"].Value);
        }
        var rest = names.Distinct(StringComparer.Ordinal).Where(n => n != def).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        if (def is not null && names.Contains(def)) rest.Insert(0, def);
        return new BranchList(rest, def);
    }
}
