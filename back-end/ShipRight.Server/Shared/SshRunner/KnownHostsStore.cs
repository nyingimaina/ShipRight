using Serilog;
using ShipRight.Shared.Store;

namespace ShipRight.Shared.SshRunner;

/// <summary>
/// Persists accepted SSH host fingerprints to ~/.shipright/known_hosts.
/// Format per line: hostname fingerprint
/// </summary>
public class KnownHostsStore
{
    private readonly string _path;
    private readonly Dictionary<string, string> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public KnownHostsStore()
    {
        _path = Path.Combine(DataDirectory.Resolve(), "known_hosts");
        Load();
    }

    public bool IsKnown(string host, string fingerprint)
    {
        lock (_lock)
            return _entries.TryGetValue(host, out var stored) && stored == fingerprint;
    }

    public string? GetStoredFingerprint(string host)
    {
        lock (_lock)
            return _entries.GetValueOrDefault(host);
    }

    public void Add(string host, string fingerprint)
    {
        lock (_lock)
        {
            _entries[host] = fingerprint;
            Persist();
        }
        Log.Information("Known hosts updated: {Host} → {Fingerprint}", host, fingerprint);
    }

    private void Load()
    {
        if (!File.Exists(_path)) return;
        foreach (var line in File.ReadAllLines(_path))
        {
            var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2) _entries[parts[0]] = parts[1];
        }
    }

    private void Persist()
    {
        var lines = _entries.Select(kvp => $"{kvp.Key} {kvp.Value}");
        File.WriteAllLines(_path, lines);
    }
}
