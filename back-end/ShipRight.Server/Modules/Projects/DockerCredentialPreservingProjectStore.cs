namespace ShipRight.Modules.Projects;

public class DockerCredentialPreservingProjectStore : IProjectStore
{
    private readonly IProjectStore _inner;

    public DockerCredentialPreservingProjectStore(IProjectStore inner)
    {
        _inner = inner;
    }

    public int Count => _inner.Count;

    public Task<List<ProjectConfig>> GetAllAsync() =>
        _inner.GetAllAsync();

    public Task<ProjectConfig?> GetByIdAsync(string id) =>
        _inner.GetByIdAsync(id);

    public Task<ProjectConfig?> GetByNameAsync(string name) =>
        _inner.GetByNameAsync(name);

    public async Task SaveAsync(ProjectConfig project)
    {
        var existing = await _inner.GetByIdAsync(project.Id);
        if (existing is not null)
        {
            project = MergeDockerCredentials(project, existing);
        }
        await _inner.SaveAsync(project);
    }

    public Task DeleteAsync(string id) =>
        _inner.DeleteAsync(id);

    private static ProjectConfig MergeDockerCredentials(ProjectConfig incoming, ProjectConfig existing)
    {
        var existingByName = existing.Services.ToDictionary(s => s.Name);
        var mergedServices = incoming.Services.Select(s =>
        {
            if (!existingByName.TryGetValue(s.Name, out var es)) return s;
            return s with
            {
                DockerUsername = string.IsNullOrEmpty(s.DockerUsername) ? es.DockerUsername : s.DockerUsername,
                DockerPassword = string.IsNullOrEmpty(s.DockerPassword) ? es.DockerPassword : s.DockerPassword,
            };
        }).ToList();

        return incoming with { Services = mergedServices };
    }
}
