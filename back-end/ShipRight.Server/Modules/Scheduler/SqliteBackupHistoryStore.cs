using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Serilog;
using ShipRight.Shared.Store;

namespace ShipRight.Modules.Scheduler;

/// <summary>
/// SQLite-backed store for backup history, replacing BackupHistoryStore
/// (scheduler/backup-history.json) in desktop mode.
/// Public API matches the original file-based store.
/// </summary>
public class SqliteBackupHistoryStore
{
    private static readonly JsonSerializerSettings JsonOpts = new()
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver(),
    };

    private readonly string _dbPath;
    private readonly List<BackupHistoryRecord> _records = [];
    private readonly object _lock = new();

    public SqliteBackupHistoryStore() : this(DataDirectory.Resolve()) { }

    internal SqliteBackupHistoryStore(string dataDir)
    {
        var dir = Path.Combine(dataDir, "scheduler");
        Directory.CreateDirectory(dir);
        _dbPath = Path.Combine(dir, "backup-history.db");
        EnsureSchema();
        MigrateFromJsonIfNeeded();
        LoadCache();
    }

    public void Append(BackupHistoryRecord record)
    {
        lock (_lock)
        {
            _records.Add(record);
            PersistUnsynchronized(record);
        }
    }

    public IReadOnlyList<BackupHistoryRecord> Query(
        DateTime? since = null, DateTime? until = null,
        string? projectId = null, string? status = null,
        int? limit = null)
    {
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

            var kept = _records
                .OrderByDescending(r => r.StartedAt)
                .Take(retainCount)
                .ToList();
            _records.Clear();
            _records.AddRange(kept);
            PersistAllUnsynchronized();
            Log.Information("Pruned backup history to {Count} records", retainCount);
        }
    }

    private void LoadCache()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT data FROM backup_history ORDER BY rowid";
        using var reader = cmd.ExecuteReader();
        lock (_lock)
        {
            _records.Clear();
            while (reader.Read())
            {
                var record = JsonConvert.DeserializeObject<BackupHistoryRecord>(reader.GetString(0), JsonOpts);
                if (record is not null)
                    _records.Add(record);
            }
        }
        Log.Information("Loaded {Count} backup history records from {Path}", _records.Count, _dbPath);
    }

    private void PersistUnsynchronized(BackupHistoryRecord record)
    {
        try
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO backup_history (id, project_id, status, data)
                VALUES ($id, $projectId, $status, $data)
                """;
            cmd.Parameters.AddWithValue("$id", record.Id);
            cmd.Parameters.AddWithValue("$projectId", record.ProjectId);
            cmd.Parameters.AddWithValue("$status", record.Status);
            cmd.Parameters.AddWithValue("$data", JsonConvert.SerializeObject(record, JsonOpts));
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to persist backup history record {RecordId}", record.Id);
        }
    }

    private void PersistAllUnsynchronized()
    {
        try
        {
            using var conn = OpenConnection();
            using var tx = conn.BeginTransaction();
            using var del = conn.CreateCommand();
            del.Transaction = tx;
            del.CommandText = "DELETE FROM backup_history";
            del.ExecuteNonQuery();

            using var insert = conn.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT INTO backup_history (id, project_id, status, data)
                VALUES ($id, $projectId, $status, $data)
                """;
            foreach (var record in _records)
            {
                insert.Parameters.Clear();
                insert.Parameters.AddWithValue("$id", record.Id);
                insert.Parameters.AddWithValue("$projectId", record.ProjectId);
                insert.Parameters.AddWithValue("$status", record.Status);
                insert.Parameters.AddWithValue("$data", JsonConvert.SerializeObject(record, JsonOpts));
                insert.ExecuteNonQuery();
            }
            tx.Commit();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to persist pruned backup history");
        }
    }

    private void EnsureSchema()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS backup_history (
                id         TEXT NOT NULL PRIMARY KEY,
                project_id TEXT NOT NULL,
                status     TEXT NOT NULL,
                data       TEXT NOT NULL
            )
            """;
        cmd.ExecuteNonQuery();
    }

    private void MigrateFromJsonIfNeeded()
    {
        var dir = Path.GetDirectoryName(_dbPath);
        var jsonPath = Path.Combine(dir!, "backup-history.json");
        if (!File.Exists(jsonPath)) return;

        try
        {
            using var conn = OpenConnection();
            using var countCmd = conn.CreateCommand();
            countCmd.CommandText = "SELECT COUNT(*) FROM backup_history";
            if ((long)(countCmd.ExecuteScalar() ?? 0L) > 0)
            {
                ArchiveJsonFile(jsonPath);
                return;
            }

            var legacy = JsonConvert.DeserializeObject<List<BackupHistoryRecord>>(
                File.ReadAllText(jsonPath), JsonOpts) ?? [];

            using var tx = conn.BeginTransaction();
            foreach (var record in legacy)
            {
                using var insertCmd = conn.CreateCommand();
                insertCmd.Transaction = tx;
                insertCmd.CommandText = """
                    INSERT OR IGNORE INTO backup_history (id, project_id, status, data)
                    VALUES ($id, $projectId, $status, $data)
                    """;
                insertCmd.Parameters.AddWithValue("$id", record.Id);
                insertCmd.Parameters.AddWithValue("$projectId", record.ProjectId);
                insertCmd.Parameters.AddWithValue("$status", record.Status);
                insertCmd.Parameters.AddWithValue("$data", JsonConvert.SerializeObject(record, JsonOpts));
                insertCmd.ExecuteNonQuery();
            }
            tx.Commit();

            Log.Information("Migrated {Count} backup history records to SQLite", legacy.Count);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to migrate backup-history.json to SQLite");
        }
        finally
        {
            if (File.Exists(jsonPath))
                ArchiveJsonFile(jsonPath);
        }
    }

    private static void ArchiveJsonFile(string jsonPath)
    {
        File.Move(jsonPath, jsonPath + ".migrated", overwrite: true);
        Log.Information("Archived backup-history.json → {Path}", jsonPath + ".migrated");
    }

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        return conn;
    }
}
