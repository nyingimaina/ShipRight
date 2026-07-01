using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Serilog;
using ShipRight.Shared.Store;

namespace ShipRight.Modules.Projects;

public class SqliteProjectStore : IProjectStore
{
    private readonly string _dbPath;
    private readonly string _dataDir;
    private List<ProjectConfig> _cache = [];
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public SqliteProjectStore() : this(DataDirectory.Resolve()) { }

    internal SqliteProjectStore(string dataDir)
    {
        _dataDir = dataDir;
        _dbPath = Path.Combine(dataDir, "projects.db");
        EnsureSchema();
        MigrateFromJsonIfNeeded();
        LoadCache();
    }

    public int Count => _cache.Count;

    public Task<List<ProjectConfig>> GetAllAsync() => Task.FromResult(_cache.ToList());

    public Task<ProjectConfig?> GetByIdAsync(string id) =>
        Task.FromResult(_cache.FirstOrDefault(p => p.Id == id));

    public Task<ProjectConfig?> GetByNameAsync(string name) =>
        Task.FromResult(_cache.FirstOrDefault(p =>
            p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)));

    public async Task SaveAsync(ProjectConfig project)
    {
        await _writeLock.WaitAsync();
        try
        {
            var idx = _cache.FindIndex(p => p.Id == project.Id);
            if (idx >= 0) _cache[idx] = project;
            else _cache.Add(project);

            var data = JsonConvert.SerializeObject(WithEncryptedPasswords(project));
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO projects (id, name, data)
                VALUES ($id, $name, $data)
                ON CONFLICT(id) DO UPDATE SET name = $name, data = $data
                """;
            cmd.Parameters.AddWithValue("$id", project.Id);
            cmd.Parameters.AddWithValue("$name", project.Name);
            cmd.Parameters.AddWithValue("$data", data);
            await cmd.ExecuteNonQueryAsync();
        }
        finally { _writeLock.Release(); }
    }

    public async Task DeleteAsync(string id)
    {
        await _writeLock.WaitAsync();
        try
        {
            _cache.RemoveAll(p => p.Id == id);
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM projects WHERE id = $id";
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
            CREATE TABLE IF NOT EXISTS projects (
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
        cmd.CommandText = "SELECT data FROM projects ORDER BY rowid";
        using var reader = cmd.ExecuteReader();
        var list = new List<ProjectConfig>();
        while (reader.Read())
        {
            var project = JsonConvert.DeserializeObject<ProjectConfig>(reader.GetString(0));
            if (project is not null)
                list.Add(WithDecryptedPasswords(project));
        }
        _cache = list;
        Log.Information("Loaded {Count} projects from {Path}", _cache.Count, _dbPath);
    }

    private void MigrateFromJsonIfNeeded()
    {
        var jsonPath = Path.Combine(_dataDir, "projects.json");
        if (!File.Exists(jsonPath)) return;

        try
        {
            // If SQLite already has rows, the migration was done on a prior run.
            using var conn = OpenConnection();
            using var countCmd = conn.CreateCommand();
            countCmd.CommandText = "SELECT COUNT(*) FROM projects";
            if ((long)(countCmd.ExecuteScalar() ?? 0L) > 0)
            {
                ArchiveJsonFile(jsonPath);
                return;
            }

            var projects = JsonConvert.DeserializeObject<List<ProjectConfig>>(
                File.ReadAllText(jsonPath)) ?? [];

            using var tx = conn.BeginTransaction();
            foreach (var project in projects)
            {
                using var insertCmd = conn.CreateCommand();
                insertCmd.Transaction = tx;
                insertCmd.CommandText = """
                    INSERT OR IGNORE INTO projects (id, name, data) VALUES ($id, $name, $data)
                    """;
                insertCmd.Parameters.AddWithValue("$id", project.Id);
                insertCmd.Parameters.AddWithValue("$name", project.Name);
                insertCmd.Parameters.AddWithValue("$data", JsonConvert.SerializeObject(project));
                insertCmd.ExecuteNonQuery();
            }
            tx.Commit();

            Log.Information("Migrated {Count} projects from projects.json to SQLite", projects.Count);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to migrate projects.json to SQLite");
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
        Log.Information("Archived projects.json → {Path}", jsonPath + ".migrated");
    }

    private ProjectConfig WithEncryptedPasswords(ProjectConfig p) => p with
    {
        Services = p.Services.Select(s => s with
        {
            DockerPassword = string.IsNullOrEmpty(s.DockerPassword)
                ? "" : SecureStore.Encrypt(s.DockerPassword, _dataDir),
        }).ToList(),
        Database = p.Database is null ? null : p.Database with
        {
            RootPassword = string.IsNullOrEmpty(p.Database.RootPassword)
                ? "" : SecureStore.Encrypt(p.Database.RootPassword, _dataDir),
        },
    };

    private ProjectConfig WithDecryptedPasswords(ProjectConfig p) => p with
    {
        Services = p.Services.Select(s => s with
        {
            DockerPassword = string.IsNullOrEmpty(s.DockerPassword)
                ? "" : SecureStore.Decrypt(s.DockerPassword, _dataDir),
        }).ToList(),
        Database = p.Database is null ? null : p.Database with
        {
            RootPassword = string.IsNullOrEmpty(p.Database.RootPassword)
                ? "" : SecureStore.Decrypt(p.Database.RootPassword, _dataDir),
        },
    };

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        return conn;
    }
}
