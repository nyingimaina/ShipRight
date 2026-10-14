using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Serilog;
using ShipRight.Shared.Store;

namespace ShipRight.Modules.Builds;

/// <summary>
/// SQLite-backed store for builds, replacing JsonBuildStore (builds/*.json)
/// in desktop mode. Public API matches the original file-based store.
/// SQLite is the source of truth — no in-memory cache — so concurrent
/// in-flight builds always read the last-persisted state.
/// </summary>
public class SqliteBuildStore : IBuildStore
{
    private readonly string _dbPath;
    private readonly string _dataDir;
    private readonly JsonSerializerSettings _settings = new()
    {
        Formatting = Formatting.Indented,
        Converters = { new StringEnumConverter() },
    };
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    // SQL text is a constant; every filter value is bound as a parameter. A null
    // parameter disables its clause, so no clause is ever assembled at runtime.
    private const string QuerySql = """
        SELECT data FROM builds
        WHERE ($projectId IS NULL OR project_id = $projectId)
          AND ($statusList IS NULL OR instr($statusList, ',' || LOWER(status) || ',') > 0)
          AND ($from IS NULL OR started_at >= $from)
          AND ($to IS NULL OR started_at <= $to)
          AND ($gitTag IS NULL OR git_tag = $gitTag)
        ORDER BY started_at DESC
        LIMIT $pageSize OFFSET $offset
        """;

    private const string CountSql = """
        SELECT COUNT(*) FROM builds
        WHERE ($projectId IS NULL OR project_id = $projectId)
          AND ($statusList IS NULL OR instr($statusList, ',' || LOWER(status) || ',') > 0)
          AND ($from IS NULL OR started_at >= $from)
          AND ($to IS NULL OR started_at <= $to)
          AND ($gitTag IS NULL OR git_tag = $gitTag)
        """;

    public SqliteBuildStore() : this(DataDirectory.Resolve()) { }

    internal SqliteBuildStore(string dataDir)
    {
        _dataDir = dataDir;
        _dbPath = Path.Combine(dataDir, "builds.db");
        EnsureSchema();
        MigrateFromJsonIfNeeded();
    }

    public int Count
    {
        get
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM builds";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    public async Task SaveAsync(BuildRecord record)
    {
        await _writeLock.WaitAsync();
        try
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO builds (id, project_id, status, git_tag, started_at, completed_at, data)
                VALUES ($id, $projectId, $status, $gitTag, $startedAt, $completedAt, $data)
                ON CONFLICT(id) DO UPDATE SET
                    project_id  = $projectId,
                    status      = $status,
                    git_tag     = $gitTag,
                    started_at  = $startedAt,
                    completed_at = $completedAt,
                    data        = $data
                """;
            cmd.Parameters.AddWithValue("$id", record.Id);
            cmd.Parameters.AddWithValue("$projectId", record.ProjectId);
            cmd.Parameters.AddWithValue("$status", record.Status.ToString());
            cmd.Parameters.AddWithValue("$gitTag", record.GitTag ?? "");
            cmd.Parameters.AddWithValue("$startedAt", record.StartedAt.ToString("o"));
            cmd.Parameters.AddWithValue("$completedAt",
                record.CompletedAt.HasValue ? record.CompletedAt.Value.ToString("o") : DBNull.Value);
            cmd.Parameters.AddWithValue("$data", JsonConvert.SerializeObject(record, _settings));
            await cmd.ExecuteNonQueryAsync();
        }
        finally { _writeLock.Release(); }
    }

    public async Task<BuildRecord?> GetByIdAsync(string id)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT data FROM builds WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return Deserialize(reader.GetString(0));
    }

    public async Task<List<BuildRecord>> QueryAsync(
        string? projectId, string? status, DateTime? from, DateTime? to, string? gitTag,
        int page, int pageSize)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = QuerySql;
        BindFilters(cmd, projectId, status, from, to, gitTag);
        cmd.Parameters.AddWithValue("$pageSize", pageSize);
        cmd.Parameters.AddWithValue("$offset", (page - 1) * pageSize);

        using var reader = await cmd.ExecuteReaderAsync();
        var results = new List<BuildRecord>();
        while (await reader.ReadAsync())
        {
            var record = Deserialize(reader.GetString(0));
            if (record is not null)
                results.Add(record);
        }
        return results;
    }

    public async Task<int> CountQueryAsync(
        string? projectId, string? status, DateTime? from, DateTime? to, string? gitTag)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = CountSql;
        BindFilters(cmd, projectId, status, from, to, gitTag);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    public async Task MarkInterruptedAsync()
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-5);
        var rows = new List<(string Id, string Data)>();

        using (var conn = OpenConnection())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, data FROM builds
                WHERE status IN ('Running', 'Deploying') AND started_at <= $cutoff
                """;
            cmd.Parameters.AddWithValue("$cutoff", cutoff.ToString("o"));
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                rows.Add((reader.GetString(0), reader.GetString(1)));
        }

        foreach (var row in rows)
        {
            try
            {
                var record = Deserialize(row.Data);
                if (record is null) continue;
                record.Status = BuildStatus.Interrupted;
                record.AppendLogLine("[ShipRight] Build marked as interrupted: process was terminated during execution.");
                await SaveAsync(record);
                Log.Warning("Build {BuildId} marked as interrupted", record.Id);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to mark build {BuildId} as interrupted", row.Id);
            }
        }
    }

    private static void BindFilters(SqliteCommand cmd, string? projectId, string? status,
        DateTime? from, DateTime? to, string? gitTag)
    {
        cmd.Parameters.AddWithValue("$projectId",
            string.IsNullOrWhiteSpace(projectId) ? (object)DBNull.Value : projectId);

        var statusList = string.IsNullOrWhiteSpace(status) ? "" : ToStatusList(status);
        cmd.Parameters.AddWithValue("$statusList",
            statusList.Length == 0 ? (object)DBNull.Value : statusList);

        cmd.Parameters.AddWithValue("$from",
            from.HasValue ? from.Value.ToString("o") : (object)DBNull.Value);

        cmd.Parameters.AddWithValue("$to",
            to.HasValue ? to.Value.ToString("o") : (object)DBNull.Value);

        cmd.Parameters.AddWithValue("$gitTag",
            string.IsNullOrWhiteSpace(gitTag) ? (object)DBNull.Value : gitTag);
    }

    // Comma-surrounded lower-case list (",running,buildfailed,") so a bound value can
    // be matched with instr() against ',' || LOWER(status) || ',' without dynamic SQL.
    private static string ToStatusList(string status)
    {
        var statuses = status.Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (statuses.Length == 0) return "";
        var lowered = new string[statuses.Length];
        for (var i = 0; i < statuses.Length; i++)
            lowered[i] = statuses[i].ToLowerInvariant();
        return "," + string.Join(",", lowered) + ",";
    }

    private BuildRecord? Deserialize(string data)
    {
        try { return JsonConvert.DeserializeObject<BuildRecord>(data, _settings); }
        catch { return null; }
    }

    private void EnsureSchema()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS builds (
                id           TEXT NOT NULL PRIMARY KEY,
                project_id   TEXT NOT NULL,
                status       TEXT NOT NULL,
                git_tag      TEXT NOT NULL,
                started_at   TEXT NOT NULL,
                completed_at TEXT NULL,
                data         TEXT NOT NULL
            )
            """;
        cmd.ExecuteNonQuery();
    }

    private void MigrateFromJsonIfNeeded()
    {
        var buildsDir = Path.Combine(_dataDir, "builds");
        if (!Directory.Exists(buildsDir)) return;

        try
        {
            // If SQLite already has rows, the migration was done on a prior run.
            using var conn = OpenConnection();
            using var countCmd = conn.CreateCommand();
            countCmd.CommandText = "SELECT COUNT(*) FROM builds";
            if ((long)(countCmd.ExecuteScalar() ?? 0L) > 0)
            {
                ArchiveBuildsDir(buildsDir);
                return;
            }

            var files = Directory.GetFiles(buildsDir, "*.json");
            using var tx = conn.BeginTransaction();
            var migrated = 0;
            foreach (var file in files)
            {
                try
                {
                    var record = Deserialize(File.ReadAllText(file));
                    if (record is null) continue;

                    using var insertCmd = conn.CreateCommand();
                    insertCmd.Transaction = tx;
                    insertCmd.CommandText = """
                        INSERT OR IGNORE INTO builds
                            (id, project_id, status, git_tag, started_at, completed_at, data)
                        VALUES ($id, $projectId, $status, $gitTag, $startedAt, $completedAt, $data)
                        """;
                    insertCmd.Parameters.AddWithValue("$id", record.Id);
                    insertCmd.Parameters.AddWithValue("$projectId", record.ProjectId);
                    insertCmd.Parameters.AddWithValue("$status", record.Status.ToString());
                    insertCmd.Parameters.AddWithValue("$gitTag", record.GitTag ?? "");
                    insertCmd.Parameters.AddWithValue("$startedAt", record.StartedAt.ToString("o"));
                    insertCmd.Parameters.AddWithValue("$completedAt",
                        record.CompletedAt.HasValue ? record.CompletedAt.Value.ToString("o") : DBNull.Value);
                    insertCmd.Parameters.AddWithValue("$data", JsonConvert.SerializeObject(record, _settings));
                    insertCmd.ExecuteNonQuery();
                    migrated++;
                }
                catch { /* skip corrupt files */ }
            }
            tx.Commit();

            Log.Information("Migrated {Count} builds from builds/ to SQLite", migrated);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to migrate builds/ to SQLite");
        }
        finally
        {
            if (Directory.Exists(buildsDir))
                ArchiveBuildsDir(buildsDir);
        }
    }

    private static void ArchiveBuildsDir(string buildsDir)
    {
        var target = buildsDir + ".migrated";
        if (Directory.Exists(target))
            Directory.Delete(target, recursive: true);
        Directory.Move(buildsDir, target);
        Log.Information("Archived builds/ → {Path}", target);
    }

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        return conn;
    }
}
