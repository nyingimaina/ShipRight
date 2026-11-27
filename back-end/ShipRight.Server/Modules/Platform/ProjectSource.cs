using ShipRight.Modules.Projects;

namespace ShipRight.Modules.Platform;

/// <summary>Where a project's files come from. A union, so local folders and repositories share one contract.</summary>
public enum ProjectSourceKind
{
    LocalFolder,
    GitRepository,
}

public sealed record ProjectSource(
    ProjectSourceKind Kind,
    string Location,
    string? Branch = null,
    string? PathPrefix = null,
    string? ProviderRepoId = null);

/// <summary>Read-only access to a project's files. It never writes and it never leaves the source's root.</summary>
public interface ISourceTree
{
    ProjectSource Source { get; }
    Task<bool> ExistsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<string>> ListFilesAsync(string relativePath = "", CancellationToken ct = default);
    Task<string?> ReadTextAsync(string relativePath, CancellationToken ct = default);
}

/// <summary>Turns a project into its source, using an explicit source first and the legacy fields as a fallback.</summary>
public static class ProjectSources
{
    public static ProjectSource? FromProject(ProjectConfig project)
    {
        if (project.Source is not null) return project.Source;

        var repo = project.GitRepos?.FirstOrDefault()?.RepoPath;
        if (!string.IsNullOrWhiteSpace(repo))
            return new ProjectSource(ProjectSourceKind.LocalFolder, repo);

        if (!string.IsNullOrWhiteSpace(project.Wsl?.WorkingDir))
            return new ProjectSource(ProjectSourceKind.LocalFolder, project.Wsl.WorkingDir);

        return null;
    }

    public static ISourceTree Tree(ProjectSource source) => source.Kind switch
    {
        ProjectSourceKind.LocalFolder => new LocalFolderSourceTree(source),
        ProjectSourceKind.GitRepository => new GitRepositorySourceTree(source),
        _ => throw new ArgumentOutOfRangeException(nameof(source), source.Kind, "Unknown source kind."),
    };
}

/// <summary>A folder on this machine. Every path is resolved inside the root before it is used.</summary>
public sealed class LocalFolderSourceTree(ProjectSource source) : ISourceTree
{
    public ProjectSource Source { get; } = source;

    public Task<bool> ExistsAsync(CancellationToken ct = default) =>
        Task.FromResult(Directory.Exists(Source.Location));

    public Task<IReadOnlyList<string>> ListFilesAsync(string relativePath = "", CancellationToken ct = default)
    {
        var root = Path.GetFullPath(Source.Location);
        var folder = Resolve(root, relativePath);
        if (!Directory.Exists(folder)) return Task.FromResult<IReadOnlyList<string>>([]);

        var files = Directory
            .EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)
            .Select(file => Path.GetRelativePath(root, file).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        return Task.FromResult<IReadOnlyList<string>>(files);
    }

    public async Task<string?> ReadTextAsync(string relativePath, CancellationToken ct = default)
    {
        var file = Resolve(Path.GetFullPath(Source.Location), relativePath);
        if (!File.Exists(file)) return null;
        return await File.ReadAllTextAsync(file, ct);
    }

    private static string Resolve(string root, string relative)
    {
        var value = relative ?? string.Empty;
        if (Path.IsPathRooted(value) || value.Split('/', '\\').Contains(".."))
            throw new ArgumentException("The path must stay inside the project folder.", nameof(relative));

        var full = Path.GetFullPath(Path.Combine(root, value));
        var inside = Path.GetRelativePath(root, full);
        if (inside.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(inside))
            throw new ArgumentException("The path must stay inside the project folder.", nameof(relative));

        return full;
    }
}

/// <summary>
/// A repository known only by its address. It has no local files until something clones it, so reading throws a clear
/// error instead of pretending the tree is empty.
/// </summary>
public sealed class GitRepositorySourceTree(ProjectSource source) : ISourceTree
{
    public ProjectSource Source { get; } = source;

    public Task<bool> ExistsAsync(CancellationToken ct = default) =>
        Task.FromResult(!string.IsNullOrWhiteSpace(Source.Location));

    public Task<IReadOnlyList<string>> ListFilesAsync(string relativePath = "", CancellationToken ct = default) =>
        Task.FromException<IReadOnlyList<string>>(new NotSupportedException(
            "A repository has no local files until it is cloned. Read provider metadata instead."));

    public Task<string?> ReadTextAsync(string relativePath, CancellationToken ct = default) =>
        Task.FromException<string?>(new NotSupportedException(
            "A repository has no local files until it is cloned. Read provider metadata instead."));
}
