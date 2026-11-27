using ShipRight.Modules.Git;

namespace ShipRight.Modules.Git;

public sealed record GitInspection(
    string Url,
    string Provider,
    string Owner,
    string Name,
    string DefaultBranch,
    bool Private,
    long? RepoId,
    IReadOnlyList<string> Branches,
    IReadOnlyList<string> ComposeFiles,
    IReadOnlyList<string> Dockerfiles,
    string? SuggestedComposeFile,
    IReadOnlyList<string> Notes);

public interface IGitInspector
{
    Task<GitInspection> InspectAsync(string url, string? token, CancellationToken ct = default);
}

/// <summary>
/// Reads a repository's shape without cloning it: the default branch, branches, compose files and Dockerfiles. It
/// never writes and never returns the token it was given.
/// </summary>
public sealed class GitInspector(IEnumerable<IGitProvider> providers) : IGitInspector
{
    public async Task<GitInspection> InspectAsync(string url, string? token, CancellationToken ct = default)
    {
        var parsed = GitUrl.Parse(url)
            ?? throw new ArgumentException(
                "That does not look like a repository address. Paste the address you clone, for example https://github.com/owner/repo.");

        var provider = providers.FirstOrDefault(candidate => candidate.CanHandle(parsed))
            ?? throw new NotSupportedException("Only GitHub repositories are supported in this step.");

        var repo = await provider.GetRepoAsync(parsed, token, ct);
        var branches = await provider.GetBranchesAsync(parsed, token, ct);
        var tree = await provider.GetTreeAsync(parsed, repo.DefaultBranch, token, ct);

        var files = tree.Where(entry => entry.Type == "blob").Select(entry => entry.Path).ToList();
        var compose = files.Where(IsCompose).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
        var dockerfiles = files.Where(IsDockerfile).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();

        var notes = new List<string>();
        if (compose.Count == 0) notes.Add("No compose file was found in the repository.");
        if (compose.Count > 1) notes.Add("More than one compose file was found; pick the one this project uses.");
        if (dockerfiles.Count > 1) notes.Add("More than one Dockerfile was found; choose the right one.");

        return new GitInspection(
            parsed.HttpsUrl,
            parsed.Provider,
            parsed.Owner,
            parsed.Name,
            repo.DefaultBranch,
            repo.Private,
            repo.RepoId,
            branches.Select(branch => branch.Name).ToList(),
            compose,
            dockerfiles,
            PickCompose(compose),
            notes);
    }

    private static string? PickCompose(IReadOnlyList<string> files)
    {
        foreach (var name in new[] { "docker-compose.yml", "docker-compose.yaml", "compose.yaml", "compose.yml" })
        {
            var exact = files.FirstOrDefault(path => path.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact;
        }

        return files.FirstOrDefault();
    }

    private static bool IsCompose(string path)
    {
        var name = Path.GetFileName(path);
        return name.Equals("docker-compose.yml", StringComparison.OrdinalIgnoreCase)
               || name.Equals("docker-compose.yaml", StringComparison.OrdinalIgnoreCase)
               || name.Equals("compose.yml", StringComparison.OrdinalIgnoreCase)
               || name.Equals("compose.yaml", StringComparison.OrdinalIgnoreCase)
               || (name.StartsWith("docker-compose.", StringComparison.OrdinalIgnoreCase)
                   && (name.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
                       || name.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)));
    }

    private static bool IsDockerfile(string path) =>
        Path.GetFileName(path).StartsWith("Dockerfile", StringComparison.OrdinalIgnoreCase);
}
