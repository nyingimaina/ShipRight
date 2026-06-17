using ShipRight.Modules.Projects;

namespace ShipRight.Modules.Servers;

public interface IServerStore
{
    Task<List<ServerConfig>> GetAllAsync();
    Task<ServerConfig?> GetByIdAsync(string id);
    Task SaveAsync(ServerConfig server);
    Task DeleteAsync(string id);
}
