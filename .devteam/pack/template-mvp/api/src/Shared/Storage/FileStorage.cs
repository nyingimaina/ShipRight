using System.Collections.Concurrent;
using System.Text.Json;

namespace MyApp.Api.Shared.Storage;

/// <summary>
/// Fast-MVP storage: each kind of record lives in one JSON file in the data folder (users.json, customers.json).
/// Good for a demo or a handful of users on one machine. It is not a database: one process only, and everything is held in memory.
/// When the app outgrows it, ask DevTeam to "add the database" (see README.md): the stores are the only code that changes.
/// </summary>
public sealed class FileStorage(string folder)
{
    private readonly ConcurrentDictionary<string, object> _tables = new();

    public string Folder => folder;

    /// <summary>The table with this name. The same instance every time, so every request sees the same data.</summary>
    public FileTable<T> Table<T>(string name) where T : class =>
        (FileTable<T>)_tables.GetOrAdd(name, _ => new FileTable<T>(Path.Combine(folder, name + ".json")));
}

/// <summary>One list of records kept in a JSON file. Reads and writes are serialized, and every write is saved before it returns.</summary>
public sealed class FileTable<T> where T : class
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly object _gate = new();
    private readonly string _path;
    private readonly Contents _contents;

    internal FileTable(string path)
    {
        _path = path;
        _contents = File.Exists(path) ? JsonSerializer.Deserialize<Contents>(File.ReadAllText(path), Json) ?? new Contents() : new Contents();
    }

    /// <summary>What is saved: the records and the next id to hand out (so an id is never reused after a delete).</summary>
    public sealed class Contents
    {
        public long NextId { get; set; } = 1;
        public List<T> Items { get; set; } = [];

        public long TakeId() => NextId++;
    }

    /// <summary>Looks at the records without changing them. Copy what you need out of them: do not keep the list.</summary>
    public TResult Read<TResult>(Func<IReadOnlyList<T>, TResult> read)
    {
        lock (_gate) return read(_contents.Items);
    }

    /// <summary>Changes the records and saves the file before returning.</summary>
    public TResult Write<TResult>(Func<Contents, TResult> change)
    {
        lock (_gate)
        {
            var result = change(_contents);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_contents, Json));
            File.Move(temp, _path, overwrite: true); // a crash mid-save never leaves a half-written file
            return result;
        }
    }
}

public static class FileStorageSetup
{
    /// <summary>The data folder comes from configuration (Data:Directory), relative to the app folder.</summary>
    public static IServiceCollection AddFileStorage(this IServiceCollection services, IConfiguration config, IHostEnvironment env) =>
        services.AddSingleton(new FileStorage(Path.GetFullPath(config["Data:Directory"] is { Length: > 0 } folder ? folder : "data", env.ContentRootPath)));
}
