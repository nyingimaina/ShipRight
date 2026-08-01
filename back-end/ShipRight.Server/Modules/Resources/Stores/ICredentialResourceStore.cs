using ShipRight.Modules.Resources.Models;

namespace ShipRight.Modules.Resources.Stores;

public interface ICredentialResourceStore
{
    int Count { get; }
    Task<List<CredentialResource>> GetAllAsync();
    Task<List<CredentialResource>> GetByProjectAsync(string projectId);
    Task<CredentialResource?> GetByIdAsync(Guid id);
    Task<CredentialResource?> GetByNameAsync(string name);
    Task SaveAsync(CredentialResource resource);
    Task DeleteAsync(Guid id);
}
