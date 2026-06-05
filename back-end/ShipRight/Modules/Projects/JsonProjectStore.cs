using Newtonsoft.Json;
using Serilog;
using ShipRight.Shared.Store;

namespace ShipRight.Modules.Projects;

public class JsonProjectStore : IProjectStore
{
    private readonly string _filePath;
    private List<ProjectConfig> _cache = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public JsonProjectStore() : this(DataDirectory.Resolve()) { }

    // Allows tests to inject a temp directory instead of the real ~/.shipright path.
    internal JsonProjectStore(string dataDir)
    {
        _filePath = Path.Combine(dataDir, "projects.json");
        try
        {
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath);
                _cache = JsonConvert.DeserializeObject<List<ProjectConfig>>(json) ?? new();
                Log.Information("Loaded {Count} projects from {Path}", _cache.Count, _filePath);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load projects from {Path} — starting with empty store", _filePath);
        }
    }

    public int Count => _cache.Count;

    public Task<List<ProjectConfig>> GetAllAsync() =>
        Task.FromResult(_cache.ToList());

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
            await PersistAsync();
        }
        finally { _writeLock.Release(); }
    }

    public async Task DeleteAsync(string id)
    {
        await _writeLock.WaitAsync();
        try
        {
            _cache.RemoveAll(p => p.Id == id);
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
