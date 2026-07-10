using ShipRight.Modules.Resources.Models;

namespace ShipRight.Modules.Resources.Stores;

public interface IScriptResourceStore
{
    int Count { get; }
    Task<List<ScriptResource>> GetAllAsync();
    Task<List<ScriptResource>> GetGlobalAsync();
    Task<List<ScriptResource>> GetByProjectAsync(Guid projectId);
    Task<ScriptResource?> GetByIdAsync(Guid id);
    Task<ScriptResource?> GetByNameAsync(string name);
    Task SaveAsync(ScriptResource resource);
    Task DeleteAsync(Guid id);
}
