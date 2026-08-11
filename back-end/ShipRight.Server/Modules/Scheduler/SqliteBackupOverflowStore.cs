using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Jattac.Libs.Tempo.Scheduling;
using Serilog;
using ShipRight.Shared.Store;

namespace ShipRight.Modules.Scheduler;

/// <summary>
/// SQLite-backed store for failed-backup overflow records, replacing
/// BackupOverflowStore (scheduler/overflow/*.json). Public API matches the
/// original file-based store.
/// </summary>
public class SqliteBackupOverflowStore
{
    private static readonly JsonSerializerSettings JsonOpts = new()
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver(),
    };

    private readonly string _dbPath;
    private readonly TempoScheduler<BackupJob> _scheduler;

    public SqliteBackupOverflowStore(string dataDir, TempoScheduler<BackupJob> scheduler)
    {
        var dir = Path.Combine(dataDir, "scheduler");
        Directory.CreateDirectory(dir);
        _dbPath = Path.Combine(dir, "overflow.db");
        _scheduler = scheduler;
        EnsureSchema();
        MigrateFromJsonIfNeeded();
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

            var id = $"{job.ProjectId}_{DateTime.UtcNow:yyyy-MM-dd_HH-mm-ss}_{Guid.NewGuid():N}";
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO backup_overflow (id, project_id, data)
                VALUES ($id, $projectId, $data)
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$projectId", job.ProjectId);
            cmd.Parameters.AddWithValue("$data", JsonConvert.SerializeObject(record, JsonOpts));
            cmd.ExecuteNonQuery();

            Log.Information("Saved overflow record for project {ProjectId}", job.ProjectId);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save overflow record for project {ProjectId}", job.ProjectId);
        }
    }

    public IReadOnlyList<OverflowRecord> List(string? projectId = null)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = string.IsNullOrWhiteSpace(projectId)
            ? "SELECT data FROM backup_overflow ORDER BY rowid"
            : "SELECT data FROM backup_overflow WHERE project_id = $projectId ORDER BY rowid";
        if (!string.IsNullOrWhiteSpace(projectId))
            cmd.Parameters.AddWithValue("$projectId", projectId);

        using var reader = cmd.ExecuteReader();
        var records = new List<OverflowRecord>();
        while (reader.Read())
        {
            try
            {
                var record = JsonConvert.DeserializeObject<OverflowRecord>(reader.GetString(0), JsonOpts);
                if (record is not null)
                    records.Add(record);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to read overflow record from {Path}", _dbPath);
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

        await DeleteRecordsAsync(projectId);
        Log.Information("Replayed {Count} overflow records for project {ProjectId}",
            records.Count, projectId ?? "(all)");
    }

    public int Count(string? projectId = null)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = string.IsNullOrWhiteSpace(projectId)
            ? "SELECT COUNT(*) FROM backup_overflow"
            : "SELECT COUNT(*) FROM backup_overflow WHERE project_id = $projectId";
        if (!string.IsNullOrWhiteSpace(projectId))
            cmd.Parameters.AddWithValue("$projectId", projectId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private async Task DeleteRecordsAsync(string? projectId)
    {
        try
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = string.IsNullOrWhiteSpace(projectId)
                ? "DELETE FROM backup_overflow"
                : "DELETE FROM backup_overflow WHERE project_id = $projectId";
            if (!string.IsNullOrWhiteSpace(projectId))
                cmd.Parameters.AddWithValue("$projectId", projectId);
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to clear overflow records for project {ProjectId}", projectId ?? "(all)");
        }
    }

    private void EnsureSchema()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS backup_overflow (
                id         TEXT NOT NULL PRIMARY KEY,
                project_id TEXT NOT NULL,
                data       TEXT NOT NULL
            )
            """;
        cmd.ExecuteNonQuery();
    }

    private void MigrateFromJsonIfNeeded()
    {
        var dir = Path.GetDirectoryName(_dbPath);
        var overflowDir = Path.Combine(dir!, "overflow");
        if (!Directory.Exists(overflowDir)) return;

        try
        {
            using var conn = OpenConnection();
            using var countCmd = conn.CreateCommand();
            countCmd.CommandText = "SELECT COUNT(*) FROM backup_overflow";
            if ((long)(countCmd.ExecuteScalar() ?? 0L) > 0)
            {
                ArchiveOverflowDir(overflowDir);
                return;
            }

            var files = Directory.GetFiles(overflowDir, "*.json");
            using var tx = conn.BeginTransaction();
            var migrated = 0;
            foreach (var file in files)
            {
                try
                {
                    var record = JsonConvert.DeserializeObject<OverflowRecord>(
                        File.ReadAllText(file), JsonOpts);
                    if (record is null) continue;

                    using var insertCmd = conn.CreateCommand();
                    insertCmd.Transaction = tx;
                    insertCmd.CommandText = """
                        INSERT OR IGNORE INTO backup_overflow (id, project_id, data)
                        VALUES ($id, $projectId, $data)
                        """;
                    insertCmd.Parameters.AddWithValue("$id", Path.GetFileNameWithoutExtension(file));
                    insertCmd.Parameters.AddWithValue("$projectId", record.Job.ProjectId);
                    insertCmd.Parameters.AddWithValue("$data", JsonConvert.SerializeObject(record, JsonOpts));
                    insertCmd.ExecuteNonQuery();
                    migrated++;
                }
                catch { /* skip corrupt files */ }
            }
            tx.Commit();

            Log.Information("Migrated {Count} overflow records to SQLite", migrated);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to migrate overflow/ to SQLite");
        }
        finally
        {
            if (Directory.Exists(overflowDir))
                ArchiveOverflowDir(overflowDir);
        }
    }

    private static void ArchiveOverflowDir(string overflowDir)
    {
        var target = overflowDir + ".migrated";
        if (Directory.Exists(target))
            Directory.Delete(target, recursive: true);
        Directory.Move(overflowDir, target);
        Log.Information("Archived overflow/ → {Path}", target);
    }

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        return conn;
    }
}
