using Newtonsoft.Json;
using Serilog;
using ShipRight.Modules.Projects;
using ShipRight.Shared.Store;

namespace ShipRight.Modules.Servers;

public class JsonServerStore : IServerStore
{
    private readonly string _filePath;
    private List<ServerConfig> _cache = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public JsonServerStore() : this(DataDirectory.Resolve()) { }

    internal JsonServerStore(string dataDir)
    {
        _filePath = Path.Combine(dataDir, "servers.json");
        try
        {
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath);
                _cache = JsonConvert.DeserializeObject<List<ServerConfig>>(json) ?? new();
                Log.Information("Loaded {Count} servers from {Path}", _cache.Count, _filePath);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load servers from {Path}", _filePath);
        }
    }

    public Task<List<ServerConfig>> GetAllAsync() =>
        Task.FromResult(_cache.ToList());

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
            await PersistAsync();
        }
        finally { _writeLock.Release(); }
    }

    public async Task DeleteAsync(string id)
    {
        await _writeLock.WaitAsync();
        try
        {
            _cache.RemoveAll(s => s.Id == id);
            await PersistAsync();
        }
        finally { _writeLock.Release(); }
    }

    private async Task PersistAsync()
    {
        var json = JsonConvert.SerializeObject(_cache, Formatting.Indented);
        await File.WriteAllTextAsync(_filePath, json);
    }
}
