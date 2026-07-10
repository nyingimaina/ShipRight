using ShipRight.Modules.Resources.Models;

namespace ShipRight.Modules.Resources.Stores;

public interface IPipelineResourceStore
{
    int Count { get; }
    Task<List<PipelineResource>> GetAllAsync();
    Task<List<PipelineResource>> GetGlobalAsync();
    Task<List<PipelineResource>> GetByProjectAsync(Guid projectId);
    Task<PipelineResource?> GetByIdAsync(Guid id);
    Task SaveAsync(PipelineResource resource);
    Task DeleteAsync(Guid id);
}
