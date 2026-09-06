using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Serilog;
using ShipRight.Shared.Store;

namespace ShipRight.Modules.WatchBranch;

/// <summary>
/// SQLite-backed store for watch-branch history, replacing WatchBranchHistoryStore
/// (watch-branch/history.json) in desktop mode.
/// Public API matches the original file-based store.
/// </summary>
public class SqliteWatchBranchHistoryStore
{
    private static readonly JsonSerializerSettings JsonOpts = new()
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver(),
    };

    private readonly string _dbPath;
    private readonly List<WatchBranchHistoryRecord> _records = [];
    private readonly object _lock = new();

    public SqliteWatchBranchHistoryStore() : this(DataDirectory.Resolve()) { }

    internal SqliteWatchBranchHistoryStore(string dataDir)
    {
        var dir = Path.Combine(dataDir, "watch-branch");
        Directory.CreateDirectory(dir);
        _dbPath = Path.Combine(dir, "watch-branch.db");
        EnsureSchema();
        MigrateFromJsonIfNeeded();
        LoadCache();
    }

    public void Append(WatchBranchHistoryRecord record)
    {
        lock (_lock)
        {
            _records.Add(record);
            PersistUnsynchronized(record);
        }
    }

    public IReadOnlyList<WatchBranchHistoryRecord> Query(
        string? projectId = null, string? status = null, int limit = 100)
    {
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

    private void LoadCache()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT data FROM watch_branch_history ORDER BY rowid";
        using var reader = cmd.ExecuteReader();
        lock (_lock)
        {
            _records.Clear();
            while (reader.Read())
            {
                var record = JsonConvert.DeserializeObject<WatchBranchHistoryRecord>(reader.GetString(0), JsonOpts);
                if (record is not null)
                    _records.Add(record);
            }
        }
        Log.Information("Loaded {Count} watch-branch history records from {Path}", _records.Count, _dbPath);
    }

    private void PersistUnsynchronized(WatchBranchHistoryRecord record)
    {
        try
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO watch_branch_history (id, project_id, status, data)
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
            Log.Error(ex, "Failed to persist watch-branch history record {RecordId}", record.Id);
        }
    }

    private void EnsureSchema()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS watch_branch_history (
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
        var jsonPath = Path.Combine(dir!, "history.json");
        if (!File.Exists(jsonPath)) return;

        try
        {
            using var conn = OpenConnection();
            using var countCmd = conn.CreateCommand();
            countCmd.CommandText = "SELECT COUNT(*) FROM watch_branch_history";
            if ((long)(countCmd.ExecuteScalar() ?? 0L) > 0)
            {
                ArchiveJsonFile(jsonPath);
                return;
            }

            var legacy = JsonConvert.DeserializeObject<List<WatchBranchHistoryRecord>>(
                File.ReadAllText(jsonPath), JsonOpts) ?? [];

            using var tx = conn.BeginTransaction();
            foreach (var record in legacy)
            {
                using var insertCmd = conn.CreateCommand();
                insertCmd.Transaction = tx;
                insertCmd.CommandText = """
                    INSERT OR IGNORE INTO watch_branch_history (id, project_id, status, data)
                    VALUES ($id, $projectId, $status, $data)
                    """;
                insertCmd.Parameters.AddWithValue("$id", record.Id);
                insertCmd.Parameters.AddWithValue("$projectId", record.ProjectId);
                insertCmd.Parameters.AddWithValue("$status", record.Status);
                insertCmd.Parameters.AddWithValue("$data", JsonConvert.SerializeObject(record, JsonOpts));
                insertCmd.ExecuteNonQuery();
            }
            tx.Commit();

            Log.Information("Migrated {Count} watch-branch history records to SQLite", legacy.Count);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to migrate watch-branch history.json to SQLite");
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
        Log.Information("Archived watch-branch history.json → {Path}", jsonPath + ".migrated");
    }

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        return conn;
    }
}
