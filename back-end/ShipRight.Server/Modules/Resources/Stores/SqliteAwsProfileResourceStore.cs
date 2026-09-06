using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Serilog;
using ShipRight.Modules.Resources.Models;
using ShipRight.Shared.Store;

namespace ShipRight.Modules.Resources.Stores;

public class SqliteAwsProfileResourceStore : IAwsProfileResourceStore
{
    private readonly string _dbPath;
    private readonly string _dataDir;
    private List<AwsProfileResource> _cache = [];
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public SqliteAwsProfileResourceStore() : this(DataDirectory.Resolve()) { }

    internal SqliteAwsProfileResourceStore(string dataDir)
    {
        _dataDir = dataDir;
        _dbPath = Path.Combine(dataDir, "resources.db");
        EnsureSchema();
        LoadCache();
    }

    public int Count => _cache.Count;

    public Task<List<AwsProfileResource>> GetAllAsync() => Task.FromResult(_cache.ToList());

    public Task<AwsProfileResource?> GetByIdAsync(Guid id) =>
        Task.FromResult(_cache.FirstOrDefault(r => r.Id == id));

    public Task<AwsProfileResource?> GetByNameAsync(string name) =>
        Task.FromResult(_cache.FirstOrDefault(r =>
            r.Name.Equals(name, StringComparison.OrdinalIgnoreCase)));

    public async Task SaveAsync(AwsProfileResource resource)
    {
        await _writeLock.WaitAsync();
        try
        {
            var idx = _cache.FindIndex(r => r.Id == resource.Id);
            if (idx >= 0) _cache[idx] = resource;
            else _cache.Add(resource);

            var data = JsonConvert.SerializeObject(WithEncryptedSecrets(resource));
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO aws_profile_resources (id, name, data)
                VALUES ($id, $name, $data)
                ON CONFLICT(id) DO UPDATE SET name = $name, data = $data
                """;
            cmd.Parameters.AddWithValue("$id", resource.Id.ToString());
            cmd.Parameters.AddWithValue("$name", resource.Name);
            cmd.Parameters.AddWithValue("$data", data);
            await cmd.ExecuteNonQueryAsync();
        }
        finally { _writeLock.Release(); }
    }

    public async Task DeleteAsync(Guid id)
    {
        await _writeLock.WaitAsync();
        try
        {
            _cache.RemoveAll(r => r.Id == id);
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM aws_profile_resources WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id.ToString());
            await cmd.ExecuteNonQueryAsync();
        }
        finally { _writeLock.Release(); }
    }

    private void EnsureSchema()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS aws_profile_resources (
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
        cmd.CommandText = "SELECT data FROM aws_profile_resources ORDER BY rowid";
        using var reader = cmd.ExecuteReader();
        var list = new List<AwsProfileResource>();
        while (reader.Read())
        {
            var resource = JsonConvert.DeserializeObject<AwsProfileResource>(reader.GetString(0));
            if (resource is not null)
                list.Add(WithDecryptedSecrets(resource));
        }
        _cache = list;
        Log.Information("Loaded {Count} AWS profile resources from {Path}", _cache.Count, _dbPath);
    }

    private AwsProfileResource WithEncryptedSecrets(AwsProfileResource r)
    {
        string Enc(string value) => string.IsNullOrEmpty(value)
            ? "" : SecureStore.Encrypt(value, _dataDir);
        return r with
        {
            SecretAccessKey = Enc(r.SecretAccessKey),
            SessionToken = Enc(r.SessionToken),
        };
    }

    private AwsProfileResource WithDecryptedSecrets(AwsProfileResource r)
    {
        string Dec(string value) => string.IsNullOrEmpty(value)
            ? "" : SecureStore.Decrypt(value, _dataDir);
        return r with
        {
            SecretAccessKey = Dec(r.SecretAccessKey),
            SessionToken = Dec(r.SessionToken),
        };
    }

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        return conn;
    }
}
