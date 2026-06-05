using Newtonsoft.Json;
using Serilog;
using ShipRight.Shared.Store;

namespace ShipRight.Modules.Builds;

public class JsonBuildStore : IBuildStore
{
    private readonly string _buildsDir;
    private readonly JsonSerializerSettings _settings = new()
    {
        Formatting = Formatting.Indented,
        Converters = { new Newtonsoft.Json.Converters.StringEnumConverter() }
    };

    private int _count;
    public int Count => _count;

    public JsonBuildStore()
    {
        _buildsDir = Path.Combine(DataDirectory.Resolve(), "builds");
        _count = Directory.GetFiles(_buildsDir, "*.json").Length;
    }

    public async Task SaveAsync(BuildRecord record)
    {
        var path = FilePath(record.Id);
        var isNew = !File.Exists(path);
        var json = JsonConvert.SerializeObject(record, _settings);
        await File.WriteAllTextAsync(path, json);
        if (isNew) Interlocked.Increment(ref _count);
    }

    public async Task<BuildRecord?> GetByIdAsync(string id)
    {
        var path = FilePath(id);
        if (!File.Exists(path)) return null;
        var json = await File.ReadAllTextAsync(path);
        return JsonConvert.DeserializeObject<BuildRecord>(json, _settings);
    }

    public async Task<List<BuildRecord>> QueryAsync(
        string? projectId, string? status, DateTime? from, DateTime? to, string? gitTag,
        int page, int pageSize)
    {
        var all = await LoadAllAsync();
        var filtered = ApplyFilters(all, projectId, status, from, to, gitTag);
        return filtered
            .OrderByDescending(b => b.StartedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();
    }

    public async Task<int> CountQueryAsync(
        string? projectId, string? status, DateTime? from, DateTime? to, string? gitTag)
    {
        var all = await LoadAllAsync();
        return ApplyFilters(all, projectId, status, from, to, gitTag).Count();
    }

    public async Task MarkInterruptedAsync()
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-5);
        var files = Directory.GetFiles(_buildsDir, "*.json");
        foreach (var f in files)
        {
            try
            {
                var json = await File.ReadAllTextAsync(f);
                var record = JsonConvert.DeserializeObject<BuildRecord>(json, _settings);
                if (record is null) continue;
                if ((record.Status == BuildStatus.Running || record.Status == BuildStatus.Deploying)
                    && record.StartedAt < cutoff)
                {
                    record.Status = BuildStatus.Interrupted;
                    record.LogOutput += "\n[ShipRight] Build marked as interrupted: process was terminated during execution.";
                    await SaveAsync(record);
                    Log.Warning("Build {BuildId} marked as interrupted", record.Id);
                }
            }
            catch { /* skip corrupt files */ }
        }
    }

    private async Task<List<BuildRecord>> LoadAllAsync()
    {
        var files = Directory.GetFiles(_buildsDir, "*.json");
        var records = new List<BuildRecord>(files.Length);
        foreach (var f in files)
        {
            try
            {
                var json = await File.ReadAllTextAsync(f);
                var r = JsonConvert.DeserializeObject<BuildRecord>(json, _settings);
                if (r is not null) records.Add(r);
            }
            catch { /* skip corrupt */ }
        }
        return records;
    }

    private static IEnumerable<BuildRecord> ApplyFilters(
        List<BuildRecord> all, string? projectId, string? status,
        DateTime? from, DateTime? to, string? gitTag)
    {
        var q = all.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(projectId))
            q = q.Where(b => b.ProjectId == projectId);
        if (!string.IsNullOrWhiteSpace(status))
        {
            var statuses = status.Split(',', StringSplitOptions.RemoveEmptyEntries);
            q = q.Where(b => statuses.Contains(b.Status.ToString(), StringComparer.OrdinalIgnoreCase));
        }
        if (from.HasValue) q = q.Where(b => b.StartedAt >= from.Value);
        if (to.HasValue)   q = q.Where(b => b.StartedAt <= to.Value);
        if (!string.IsNullOrWhiteSpace(gitTag)) q = q.Where(b => b.GitTag == gitTag);
        return q;
    }

    private string FilePath(string id) => Path.Combine(_buildsDir, $"{id}.json");
}
