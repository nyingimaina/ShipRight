namespace ShipRight.Modules.Projects;

public static class BuildSourceExtensions
{
    /// <summary>True only when the project explicitly opted in; everything else builds in place as before.</summary>
    public static bool UsesCleanSnapshot(this ProjectConfig project) => project.BuildSource == BuildSource.CleanSnapshot;
}

/// <summary>
/// Saving a project from a client that does not send a build source (the original wizard) must not
/// quietly switch clean-snapshot builds off, so the stored choice is kept unless one is sent.
/// </summary>
public class BuildSourcePreservingProjectStore : IProjectStore
{
    private readonly IProjectStore _inner;

    public BuildSourcePreservingProjectStore(IProjectStore inner) => _inner = inner;

    public int Count => _inner.Count;

    public Task<List<ProjectConfig>> GetAllAsync() => _inner.GetAllAsync();

    public Task<ProjectConfig?> GetByIdAsync(string id) => _inner.GetByIdAsync(id);

    public Task<ProjectConfig?> GetByNameAsync(string name) => _inner.GetByNameAsync(name);

    public async Task SaveAsync(ProjectConfig project)
    {
        if (project.BuildSource is null)
        {
            var existing = await _inner.GetByIdAsync(project.Id);
            if (existing?.BuildSource is not null)
                project = project with { BuildSource = existing.BuildSource };
        }
        await _inner.SaveAsync(project);
    }

    public Task DeleteAsync(string id) => _inner.DeleteAsync(id);
}
