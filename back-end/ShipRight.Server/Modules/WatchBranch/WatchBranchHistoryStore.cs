using System.Text.Json;
using Serilog;

namespace ShipRight.Modules.WatchBranch;

public class WatchBranchHistoryStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _filePath;
    private readonly object _lock = new();
    private List<WatchBranchHistoryRecord> _records = [];
    private DateTime _lastLoad = DateTime.MinValue;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(10);

    public WatchBranchHistoryStore(string dataDir)
    {
        var dir = Path.Combine(dataDir, "watch-branch");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "history.json");
    }

    public void Append(WatchBranchHistoryRecord record)
    {
        lock (_lock)
        {
            _records.Add(record);
            PersistUnsynchronized();
        }
    }

    public IReadOnlyList<WatchBranchHistoryRecord> Query(
        string? projectId = null, string? status = null, int limit = 100)
    {
        RefreshCache();
        lock (_lock)
        {
            var q = _records.AsEnumerable();
            if (!string.IsNullOrWhiteSpace(projectId))
                q = q.Where(r => r.ProjectId == projectId);
            if (!string.IsNullOrWhiteSpace(status))
                q = q.Where(r => string.Equals(r.Status, status, StringComparison.OrdinalIgnoreCase));
            return q.OrderByDescending(r => r.TriggeredAt).Take(limit).ToList();
        }
    }

    private void RefreshCache()
    {
        if (!File.Exists(_filePath)) return;
        var lastWrite = File.GetLastWriteTimeUtc(_filePath);
        lock (_lock)
        {
            if (lastWrite <= _lastLoad) return;
            try
            {
                var json = File.ReadAllText(_filePath);
                _records = JsonSerializer.Deserialize<List<WatchBranchHistoryRecord>>(json, JsonOpts) ?? [];
                _lastLoad = lastWrite;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to reload watch-branch history");
            }
        }
    }

    private void PersistUnsynchronized()
    {
        try
        {
            var tempPath = _filePath + ".tmp";
            var json = JsonSerializer.Serialize(_records, JsonOpts);
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _filePath, overwrite: true);
            _lastLoad = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to persist watch-branch history");
        }
    }
}
