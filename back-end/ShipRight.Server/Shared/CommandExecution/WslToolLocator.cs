using System.Collections.Concurrent;
using ShipRight.Shared.ProcessRunner;

namespace ShipRight.Shared.CommandExecution;

public sealed class WslToolLocator : IWslToolLocator
{
    private static readonly TimeSpan LocateTimeout = TimeSpan.FromSeconds(15);

    private readonly IProcessRunner _runner;
    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _cache = new();

    public WslToolLocator(IProcessRunner runner)
    {
        _runner = runner;
    }

    public Task<string?> LocateAsync(string tool, bool refresh = false, CancellationToken ct = default)
    {
        if (refresh)
            _cache.TryRemove(tool, out _);

        return _cache.GetOrAdd(tool, _ => new Lazy<Task<string?>>(() => LocateCoreAsync(tool, ct))).Value;
    }

    private async Task<string?> LocateCoreAsync(string tool, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tool) || tool.Contains('/'))
            return tool;

        try
        {
            var result = await _runner.RunAsync(
                "bash", ["-lc", $"command -v {tool}"], null, timeout: LocateTimeout, ct: ct);
            var line = result.StdOut?.Trim();
            return string.IsNullOrEmpty(line) ? null : line;
        }
        catch
        {
            return null;
        }
    }
}