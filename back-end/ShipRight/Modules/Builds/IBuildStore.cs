namespace ShipRight.Modules.Builds;

public interface IBuildStore
{
    int Count { get; }
    Task SaveAsync(BuildRecord record);
    Task<BuildRecord?> GetByIdAsync(string id);
    Task<List<BuildRecord>> QueryAsync(string? projectId, string? status, DateTime? from, DateTime? to, string? gitTag, int page, int pageSize);
    Task<int> CountQueryAsync(string? projectId, string? status, DateTime? from, DateTime? to, string? gitTag);
    Task MarkInterruptedAsync();
}
