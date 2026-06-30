using System.Text.Json;
using Jattac.Libs.Tempo.Scheduling;
using Serilog;

namespace ShipRight.Modules.Scheduler;

public class BackupOverflowStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _overflowDir;
    private readonly TempoScheduler<BackupJob> _scheduler;

    public BackupOverflowStore(string dataDir, TempoScheduler<BackupJob> scheduler)
    {
        _overflowDir = Path.Combine(dataDir, "scheduler", "overflow");
        Directory.CreateDirectory(_overflowDir);
        _scheduler = scheduler;
    }

    public void Save(BackupJob job, string errorMessage)
    {
        try
        {
            var record = new OverflowRecord
            {
                Job = job,
                FailedAt = DateTime.UtcNow,
                ErrorMessage = errorMessage,
            };

            var fileName = $"{job.ProjectId}_{DateTime.UtcNow:yyyy-MM-dd_HH-mm-ss}_{Guid.NewGuid():N}.json";
            var filePath = Path.Combine(_overflowDir, fileName);
            var json = JsonSerializer.Serialize(record, JsonOpts);
            File.WriteAllText(filePath, json);

            Log.Information("Saved overflow record for project {ProjectId}: {File}", job.ProjectId, fileName);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save overflow record for project {ProjectId}", job.ProjectId);
        }
    }

    public IReadOnlyList<OverflowRecord> List(string? projectId = null)
    {
        if (!Directory.Exists(_overflowDir)) return [];

        var files = Directory.GetFiles(_overflowDir, "*.json");

        var records = new List<OverflowRecord>();
        foreach (var file in files)
        {
            try
            {
                var json = File.ReadAllText(file);
                var record = JsonSerializer.Deserialize<OverflowRecord>(json, JsonOpts);
                if (record is not null)
                {
                    if (string.IsNullOrWhiteSpace(projectId) || record.Job.ProjectId == projectId)
                        records.Add(record);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to read overflow file {File}", file);
            }
        }

        return records.OrderByDescending(r => r.FailedAt).ToList();
    }

    public async Task ReplayAsync(string? projectId = null)
    {
        var records = List(projectId);

        if (records.Count == 0)
        {
            Log.Information("No overflow records to replay for project {ProjectId}", projectId ?? "(all)");
            return;
        }

        foreach (var record in records)
        {
            _scheduler.Register(
                record.Job,
                new Jattac.Libs.Tempo.Scheduling.TempoSchedule.OnceAt(DateTimeOffset.UtcNow),
                Jattac.Libs.Tempo.Scheduling.MissedRunPolicy.Skip,
                Jattac.Libs.Tempo.Scheduling.OverlapPolicy.Skip);
        }

        DeleteFiles(projectId);
        Log.Information("Replayed {Count} overflow records for project {ProjectId}",
            records.Count, projectId ?? "(all)");
    }

    public int Count(string? projectId = null)
    {
        if (!Directory.Exists(_overflowDir)) return 0;
        if (string.IsNullOrWhiteSpace(projectId))
            return Directory.GetFiles(_overflowDir, "*.json").Length;

        return Directory.GetFiles(_overflowDir, "*.json")
            .Count(f => Path.GetFileName(f).StartsWith(projectId + "_", StringComparison.OrdinalIgnoreCase));
    }

    private void DeleteFiles(string? projectId)
    {
        if (!Directory.Exists(_overflowDir)) return;

        var files = string.IsNullOrWhiteSpace(projectId)
            ? Directory.GetFiles(_overflowDir, "*.json")
            : Directory.GetFiles(_overflowDir, $"{projectId}_*.json");

        foreach (var file in files)
        {
            try { File.Delete(file); }
            catch { /* best-effort */ }
        }
    }
}

public record OverflowRecord
{
    public BackupJob Job { get; init; } = null!;
    public DateTime FailedAt { get; init; }
    public string ErrorMessage { get; init; } = string.Empty;
}
