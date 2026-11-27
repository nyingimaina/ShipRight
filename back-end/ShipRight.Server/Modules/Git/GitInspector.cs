using ShipRight.Modules.Git;

using System.Text.RegularExpressions;
using ShipRight.Modules.DockerAdoption;

namespace ShipRight.Modules.Git;

public sealed record GitSuggestedService(
    string Name,
    string Image,
    string Dockerfile,
    string Context,
    string? Target = null,
    IReadOnlyDictionary<string, string>? Args = null);

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
    IReadOnlyList<GitSuggestedService> Services,
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

        var suggested = PickCompose(compose);
        var services = new List<GitSuggestedService>();
        var supporting = new List<string>();

        if (suggested is not null)
        {
            try
            {
                var text = await provider.GetFileAsync(parsed, repo.DefaultBranch, suggested, token, ct);
                if (text is not null)
                {
                    foreach (var service in ComposeLite.Parse(text))
                    {
                        if (service.Build is null)
                        {
                            supporting.Add(service.Name);
                            continue;
                        }

                        services.Add(new GitSuggestedService(
                            service.Name,
                            ImageFor(service, repo.Name),
                            string.IsNullOrWhiteSpace(service.Build.Dockerfile) ? "Dockerfile" : service.Build.Dockerfile!,
                            string.IsNullOrWhiteSpace(service.Build.Context) ? "." : service.Build.Context!,
                            service.Build.Target,
                            service.Build.Args.Count > 0
                                ? new Dictionary<string, string>(service.Build.Args, StringComparer.Ordinal)
                                : null));
                    }
                }
            }
            catch (GitProviderException ex)
            {
                notes.Add($"The compose file could not be read: {ex.Message}");
            }
        }

        if (supporting.Count > 0)
            notes.Add($"Supporting services (not built): {string.Join(", ", supporting)}.");
        if (suggested is not null && services.Count == 0 && supporting.Count == 0)
            notes.Add("The compose file did not list any services.");

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
            services,
            suggested,
            notes);
    }

    private static string ImageFor(ComposeServiceInfo service, string repoName)
    {
        var image = DockerSetupAnalyzer.ImageName(service.Image)
                    ?? ImageFromVariable(service.Image);
        if (!string.IsNullOrWhiteSpace(image)) return image!.ToLowerInvariant();
        return $"{Slug(repoName)}/{Slug(service.Name)}";
    }

    /// <summary>Reads the fallback image inside <c>${TAG:-repo/name:latest}</c>.</summary>
    private static string? ImageFromVariable(string? image)
    {
        var match = Regex.Match(image ?? string.Empty, @"^\$\{[A-Za-z_][A-Za-z0-9_]*:-(?<default>[^}]*)\}$");
        return match.Success ? DockerSetupAnalyzer.ImageName(match.Groups["default"].Value) : null;
    }

    private static string Slug(string value)
    {
        var slug = Regex.Replace((value ?? string.Empty).ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return slug.Length > 0 ? slug : "app";
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
