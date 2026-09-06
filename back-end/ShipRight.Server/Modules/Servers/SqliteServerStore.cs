using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Serilog;
using ShipRight.Modules.Projects;
using ShipRight.Shared.Store;

namespace ShipRight.Modules.Servers;

/// <summary>
/// SQLite-backed store for servers, replacing JsonServerStore (servers.json)
/// in desktop mode. Public API matches the original file-based store.
/// </summary>
public class SqliteServerStore : IServerStore
{
    private readonly string _dbPath;
    private readonly string _dataDir;
    private List<ServerConfig> _cache = [];
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public SqliteServerStore() : this(DataDirectory.Resolve()) { }

    internal SqliteServerStore(string dataDir)
    {
        _dataDir = dataDir;
        _dbPath = Path.Combine(dataDir, "servers.db");
        EnsureSchema();
        MigrateFromJsonIfNeeded();
        LoadCache();
    }

    public Task<List<ServerConfig>> GetAllAsync() => Task.FromResult(_cache.ToList());

    public Task<ServerConfig?> GetByIdAsync(string id) =>
        Task.FromResult(_cache.FirstOrDefault(s => s.Id == id));

    public async Task SaveAsync(ServerConfig server)
    {
        await _writeLock.WaitAsync();
        try
        {
            var idx = _cache.FindIndex(s => s.Id == server.Id);
            if (idx >= 0) _cache[idx] = server;
            else _cache.Add(server);

            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO servers (id, name, data)
                VALUES ($id, $name, $data)
                ON CONFLICT(id) DO UPDATE SET name = $name, data = $data
                """;
            cmd.Parameters.AddWithValue("$id", server.Id);
            cmd.Parameters.AddWithValue("$name", server.Name);
            cmd.Parameters.AddWithValue("$data", JsonConvert.SerializeObject(server));
            await cmd.ExecuteNonQueryAsync();
        }
        finally { _writeLock.Release(); }
    }

    public async Task DeleteAsync(string id)
    {
        await _writeLock.WaitAsync();
        try
        {
            _cache.RemoveAll(s => s.Id == id);
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM servers WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync();
        }
        finally { _writeLock.Release(); }
    }

    private void EnsureSchema()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS servers (
                id   TEXT NOT NULL PRIMARY KEY,
                name TEXT NOT NULL,
                data TEXT NOT NULL
            )
            """;
        cmd.ExecuteNonQuery();
    }

    private void LoadCache()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT data FROM servers ORDER BY rowid";
        using var reader = cmd.ExecuteReader();
        var list = new List<ServerConfig>();
        while (reader.Read())
        {
            var server = JsonConvert.DeserializeObject<ServerConfig>(reader.GetString(0));
            if (server is not null)
                list.Add(server);
        }
        _cache = list;
        Log.Information("Loaded {Count} servers from {Path}", _cache.Count, _dbPath);
    }

    private void MigrateFromJsonIfNeeded()
    {
        var jsonPath = Path.Combine(_dataDir, "servers.json");
        if (!File.Exists(jsonPath)) return;

        try
        {
            // If SQLite already has rows, the migration was done on a prior run.
            using var conn = OpenConnection();
            using var countCmd = conn.CreateCommand();
            countCmd.CommandText = "SELECT COUNT(*) FROM servers";
            if ((long)(countCmd.ExecuteScalar() ?? 0L) > 0)
            {
                ArchiveJsonFile(jsonPath);
                return;
            }

            var servers = JsonConvert.DeserializeObject<List<ServerConfig>>(
                File.ReadAllText(jsonPath)) ?? [];

            using var tx = conn.BeginTransaction();
            foreach (var server in servers)
            {
                using var insertCmd = conn.CreateCommand();
                insertCmd.Transaction = tx;
                insertCmd.CommandText = """
                    INSERT OR IGNORE INTO servers (id, name, data) VALUES ($id, $name, $data)
                    """;
                insertCmd.Parameters.AddWithValue("$id", server.Id);
                insertCmd.Parameters.AddWithValue("$name", server.Name);
                insertCmd.Parameters.AddWithValue("$data", JsonConvert.SerializeObject(server));
                insertCmd.ExecuteNonQuery();
            }
            tx.Commit();

            Log.Information("Migrated {Count} servers from servers.json to SQLite", servers.Count);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to migrate servers.json to SQLite");
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
        Log.Information("Archived servers.json → {Path}", jsonPath + ".migrated");
    }

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        return conn;
    }
}
