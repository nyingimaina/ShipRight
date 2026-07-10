using ShipRight.Modules.Resources.Models;

namespace ShipRight.Modules.Resources.Stores;

public interface IDockerRegistryResourceStore
{
    int Count { get; }
    Task<List<DockerRegistryResource>> GetAllAsync();
    Task<DockerRegistryResource?> GetByIdAsync(Guid id);
    Task<DockerRegistryResource?> GetByNameAsync(string name);
    Task SaveAsync(DockerRegistryResource resource);
    Task DeleteAsync(Guid id);
}
