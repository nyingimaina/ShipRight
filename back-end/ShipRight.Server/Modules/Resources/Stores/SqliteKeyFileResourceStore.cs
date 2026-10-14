using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Serilog;
using ShipRight.Modules.Resources.Models;
using ShipRight.Shared.Store;

namespace ShipRight.Modules.Resources.Stores;

// Desktop store: keeps key files in a small SQLite file, with the .pem content encrypted at rest
// through the same SecureStore the credential store uses (R10). Paging is done over the in-memory
// cache, which is bounded by the single-user desktop data set.
public class SqliteKeyFileResourceStore : IKeyFileResourceStore
{
    private readonly string _dbPath;
    private readonly string _dataDir;
    private List<KeyFileResource> _cache = [];
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public SqliteKeyFileResourceStore() : this(DataDirectory.Resolve()) { }

    internal SqliteKeyFileResourceStore(string dataDir)
    {
        _dataDir = dataDir;
        _dbPath = Path.Combine(dataDir, "key_files.db");
        EnsureSchema();
        LoadCache();
    }

    public Task<(List<KeyFileResource> Items, int Total)> GetPageAsync(int page, int pageSize)
    {
        var ordered = _cache.OrderBy(r => r.CreatedAt).ThenBy(r => r.Id).ToList();
        var items = ordered.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return Task.FromResult((items, ordered.Count));
    }

    public Task<KeyFileResource?> GetByIdAsync(Guid id) =>
        Task.FromResult(_cache.FirstOrDefault(r => r.Id == id));

    public async Task SaveAsync(KeyFileResource resource)
    {
        await _writeLock.WaitAsync();
        try
        {
            var data = JsonConvert.SerializeObject(WithEncryptedContent(resource));
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO key_file_resources (id, name, data)
                VALUES ($id, $name, $data)
                ON CONFLICT(id) DO UPDATE SET name = $name, data = $data
                """;
            cmd.Parameters.AddWithValue("$id", resource.Id.ToString());
            cmd.Parameters.AddWithValue("$name", resource.Name);
            cmd.Parameters.AddWithValue("$data", data);
            await cmd.ExecuteNonQueryAsync();

            // The cache is updated only after the row is written, so a failed save never leaves a
            // half-created key file visible in the list (R5).
            var idx = _cache.FindIndex(r => r.Id == resource.Id);
            if (idx >= 0) _cache[idx] = resource;
            else _cache.Add(resource);
        }
        finally { _writeLock.Release(); }
    }

    public async Task DeleteAsync(Guid id)
    {
        await _writeLock.WaitAsync();
        try
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM key_file_resources WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id.ToString());
            await cmd.ExecuteNonQueryAsync();

            // Only forget the row once the delete succeeded, so a failed delete does not make a
            // still-stored key file disappear from the list (R7).
            _cache.RemoveAll(r => r.Id == id);
        }
        finally { _writeLock.Release(); }
    }

    private void EnsureSchema()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS key_file_resources (
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
        cmd.CommandText = "SELECT data FROM key_file_resources ORDER BY rowid";
        using var reader = cmd.ExecuteReader();
        var list = new List<KeyFileResource>();
        while (reader.Read())
        {
            var resource = JsonConvert.DeserializeObject<KeyFileResource>(reader.GetString(0));
            if (resource is not null)
                list.Add(WithDecryptedContent(resource));
        }
        _cache = list;
        Log.Information("Loaded {Count} key file resources from {Path}", _cache.Count, _dbPath);
    }

    private KeyFileResource WithEncryptedContent(KeyFileResource r) => r with
    {
        Content = string.IsNullOrEmpty(r.Content) ? "" : SecureStore.Encrypt(r.Content, _dataDir),
    };

    private KeyFileResource WithDecryptedContent(KeyFileResource r) => r with
    {
        Content = string.IsNullOrEmpty(r.Content) ? "" : SecureStore.Decrypt(r.Content, _dataDir),
    };

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        return conn;
    }
}
