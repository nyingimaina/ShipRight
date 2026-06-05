namespace ShipRight.Modules.Projects;

public interface IProjectStore
{
    int Count { get; }
    Task<List<ProjectConfig>> GetAllAsync();
    Task<ProjectConfig?> GetByIdAsync(string id);
    Task<ProjectConfig?> GetByNameAsync(string name);
    Task SaveAsync(ProjectConfig project);
    Task DeleteAsync(string id);
}
