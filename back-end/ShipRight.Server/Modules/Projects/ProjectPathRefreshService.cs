namespace ShipRight.Modules.Projects;

/// <summary>Plan is always set. Project is set by Apply only (the project as saved).</summary>
public record PathRefreshOutcome(PathRefreshPlan Plan, ProjectConfig? Project);

/// <summary>
/// Re-detects a stored project's service folders and updates only their version-file and
/// build-context paths. It reads the stored project (already decrypted), changes those two
/// fields, and saves the same object back, so nothing the client never sends can be lost.
/// </summary>
public class ProjectPathRefreshService
{
    private readonly IProjectStore _store;
    private readonly Func<string, DetectedProjectConfig> _detect;

    public ProjectPathRefreshService(IProjectStore store, Func<string, DetectedProjectConfig> detect)
    {
        _store = store;
        _detect = detect;
    }

    public async Task<PathRefreshOutcome?> PreviewAsync(string projectId, string? rootPath)
    {
        var project = await _store.GetByIdAsync(projectId);
        if (project is null) return null;

        var plan = ProjectPathRefresher.Plan(project, _detect(ResolveRoot(project, rootPath)));
        return new PathRefreshOutcome(plan, null);
    }

    public async Task<PathRefreshOutcome?> ApplyAsync(string projectId, string? rootPath)
    {
        var project = await _store.GetByIdAsync(projectId);
        if (project is null) return null;

        var plan = ProjectPathRefresher.Plan(project, _detect(ResolveRoot(project, rootPath)));
        if (plan.Changes.Count == 0) return new PathRefreshOutcome(plan, project);

        var updated = ProjectPathRefresher.Apply(project, plan);
        updated.ModifiedAt = DateTime.UtcNow;
        await _store.SaveAsync(updated);
        return new PathRefreshOutcome(plan, updated);
    }

    private static string ResolveRoot(ProjectConfig project, string? rootPath)
    {
        var root = string.IsNullOrWhiteSpace(rootPath)
            ? project.GitRepos.FirstOrDefault()?.RepoPath
            : rootPath.Trim();

        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("Enter the project's root folder first.");
        if (root.Contains("..") || root.Contains('\0'))
            throw new ArgumentException("Invalid path.");
        if (!Directory.Exists(root))
            throw new ArgumentException($"Directory not found: {root}");
        return root;
    }
}
