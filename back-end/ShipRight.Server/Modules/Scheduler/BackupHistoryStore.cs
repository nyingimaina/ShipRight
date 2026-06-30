using System.Text.Json;
using Serilog;

namespace ShipRight.Modules.Scheduler;

public class BackupHistoryStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _filePath;
    private readonly object _lock = new();
    private List<BackupHistoryRecord> _records = [];
    private DateTime _lastLoad = DateTime.MinValue;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(10);

    public BackupHistoryStore(string dataDir)
    {
        var dir = Path.Combine(dataDir, "scheduler");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "backup-history.json");
    }

    public void Append(BackupHistoryRecord record)
    {
        lock (_lock)
        {
            _records.Add(record);
            PersistUnsynchronized();
        }
    }

    public IReadOnlyList<BackupHistoryRecord> Query(
        DateTime? since = null, DateTime? until = null,
        string? projectId = null, string? status = null,
        int? limit = null)
    {
        RefreshCache();

        lock (_lock)
        {
            var query = _records.AsEnumerable();

            if (since.HasValue)
                query = query.Where(r => r.StartedAt >= since.Value);
            if (until.HasValue)
                query = query.Where(r => r.StartedAt <= until.Value);
            if (!string.IsNullOrWhiteSpace(projectId))
                query = query.Where(r => r.ProjectId == projectId);
            if (!string.IsNullOrWhiteSpace(status))
                query = query.Where(r =>
                    string.Equals(r.Status, status, StringComparison.OrdinalIgnoreCase));

            query = query.OrderByDescending(r => r.StartedAt);

            if (limit.HasValue)
                query = query.Take(limit.Value);

            return query.ToList();
        }
    }

    public BackupReportSummary GetSummary(DateTime? since = null, DateTime? until = null)
    {
        RefreshCache();

        lock (_lock)
        {
            var query = _records.AsEnumerable();

            since ??= DateTime.UtcNow.AddDays(-30);
            until ??= DateTime.UtcNow;

            query = query.Where(r => r.StartedAt >= since.Value && r.StartedAt <= until.Value);
            var filtered = query.ToList();

            var successful = filtered.Count(r => r.Status == "completed");
            var failed = filtered.Count(r => r.Status == "failed");
            var total = filtered.Count;

            return new BackupReportSummary
            {
                TotalRuns = total,
                SuccessfulRuns = successful,
                FailedRuns = failed,
                SuccessRate = total > 0 ? Math.Round((double)successful / total * 100, 1) : 100,
                AvgDurationMs = total > 0 ? Math.Round(filtered.Average(r => r.DurationMs), 0) : 0,
                TotalSizeBytes = filtered.Sum(r => r.BackupSizeBytes),
                From = since.Value,
                To = until.Value,
            };
        }
    }

    public List<BackupDailyReport> GetDailyReport(int days = 30)
    {
        RefreshCache();

        var since = DateTime.UtcNow.Date.AddDays(-days);
        var until = DateTime.UtcNow.Date.AddDays(1);

        lock (_lock)
        {
            var byDay = _records
                .Where(r => r.StartedAt >= since && r.StartedAt < until)
                .GroupBy(r => r.StartedAt.Date)
                .ToList();

            var results = new List<BackupDailyReport>();

            for (var date = since; date < until; date = date.AddDays(1))
            {
                var day = byDay.FirstOrDefault(g => g.Key == date);
                if (day is null)
                {
                    results.Add(new BackupDailyReport { Date = date });
                    continue;
                }

                var dayList = day.ToList();
                results.Add(new BackupDailyReport
                {
                    Date = date,
                    TotalRuns = dayList.Count,
                    SuccessfulRuns = dayList.Count(r => r.Status == "completed"),
                    FailedRuns = dayList.Count(r => r.Status == "failed"),
                    AvgDurationMs = Math.Round(dayList.Average(r => r.DurationMs), 0),
                    TotalSizeBytes = dayList.Sum(r => r.BackupSizeBytes),
                });
            }

            return results;
        }
    }

    public List<BackupProjectReport> GetProjectReports()
    {
        RefreshCache();

        lock (_lock)
        {
            var byProject = _records
                .GroupBy(r => r.ProjectId)
                .ToList();

            return byProject.Select(g =>
            {
                var list = g.ToList();
                var successful = list.Count(r => r.Status == "completed");
                var total = list.Count;
                return new BackupProjectReport
                {
                    ProjectId = g.Key,
                    ProjectName = list.First().ProjectName,
                    TotalRuns = total,
                    SuccessfulRuns = successful,
                    FailedRuns = total - successful,
                    SuccessRate = total > 0 ? Math.Round((double)successful / total * 100, 1) : 100,
                    LastBackupAt = list.Max(r => r.StartedAt),
                };
            }).OrderByDescending(r => r.LastBackupAt).ToList();
        }
    }

    public void Prune(int retainCount = 1000)
    {
        lock (_lock)
        {
            if (_records.Count <= retainCount) return;

            _records = _records
                .OrderByDescending(r => r.StartedAt)
                .Take(retainCount)
                .OrderByDescending(r => r.StartedAt)
                .ToList();

            PersistUnsynchronized();
            Log.Information("Pruned backup history to {Count} records", retainCount);
        }
    }

    private void RefreshCache()
    {
        if (!File.Exists(_filePath)) return;
        var lastWrite = File.GetLastWriteTimeUtc(_filePath);

        lock (_lock)
        {
            if (lastWrite <= _lastLoad) return;
            ReloadUnsynchronized();
            _lastLoad = lastWrite;
        }
    }

    private void ReloadUnsynchronized()
    {
        try
        {
            var json = File.ReadAllText(_filePath);
            _records = JsonSerializer.Deserialize<List<BackupHistoryRecord>>(json, JsonOpts) ?? [];
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to reload backup history, using in-memory state");
        }
    }

    private void PersistUnsynchronized()
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (dir is not null) Directory.CreateDirectory(dir);

            var tempPath = _filePath + ".tmp";
            var json = JsonSerializer.Serialize(_records, JsonOpts);
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _filePath, overwrite: true);
            _lastLoad = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to persist backup history");
        }
    }
}
