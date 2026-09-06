using ShipRight.Modules.Resources.Models;

namespace ShipRight.Modules.Resources.Stores;

public interface IAwsProfileResourceStore
{
    int Count { get; }
    Task<List<AwsProfileResource>> GetAllAsync();
    Task<AwsProfileResource?> GetByIdAsync(Guid id);
    Task<AwsProfileResource?> GetByNameAsync(string name);
    Task SaveAsync(AwsProfileResource resource);
    Task DeleteAsync(Guid id);
}
