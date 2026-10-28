using ShipRight.Shared.ProcessRunner;

namespace ShipRight.Tests.Modules.Setup;

/// <summary>A throwaway folder tree for detection tests; deleted on dispose.</summary>
internal sealed class TempTree : IDisposable
{
    public string Root { get; }

    public TempTree()
    {
        Root = Path.Combine(Path.GetTempPath(), $"sr_setup_{Guid.NewGuid():N}", "proj");
        Directory.CreateDirectory(Root);
    }

    public string Add(string relativePath, string content = "")
    {
        var full = Full(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    public string Dir(string relativePath)
    {
        var full = Full(relativePath);
        Directory.CreateDirectory(full);
        return full;
    }

    public string Full(string relativePath) =>
        Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));

    public void Dispose()
    {
        var sandbox = Path.GetDirectoryName(Root)!;
        try { Directory.Delete(sandbox, recursive: true); } catch { /* best effort */ }
    }
}

/// <summary>Records every process call; replies from the first matching rule, else exit 0 with no output.</summary>
internal sealed class RecordingRunner : IProcessRunner
{
    private readonly List<(Func<string, string[], bool> Match, ProcessResult Result)> _rules = [];

    public List<(string Exe, string[] Args)> Calls { get; } = [];

    public RecordingRunner When(Func<string, string[], bool> match, int exit = 0, string stdout = "", string stderr = "")
    {
        _rules.Add((match, new ProcessResult(exit, stdout, stderr, TimeSpan.Zero)));
        return this;
    }

    /// <summary>Like <see cref="When"/> but takes priority over every rule added so far (rules are first-match).</summary>
    public RecordingRunner WhenFirst(Func<string, string[], bool> match, int exit = 0, string stdout = "", string stderr = "")
    {
        _rules.Insert(0, (match, new ProcessResult(exit, stdout, stderr, TimeSpan.Zero)));
        return this;
    }

    public RecordingRunner WhenArgs(string token, int exit = 0, string stdout = "", string stderr = "") =>
        When((_, a) => a.Contains(token), exit, stdout, stderr);

    public Task<ProcessResult> RunAsync(
        string executable, string[] args, string? workingDir,
        Func<string, Task>? onOutput = null, Func<string, Task>? onError = null,
        CancellationToken ct = default, IReadOnlyDictionary<string, string>? envOverride = null,
        TimeSpan? timeout = null, string? stdin = null)
    {
        Calls.Add((executable, args));
        foreach (var (match, result) in _rules)
            if (match(executable, args)) return Task.FromResult(result);
        return Task.FromResult(new ProcessResult(0, "", "", TimeSpan.Zero));
    }

    public string Joined(int i) => $"{Calls[i].Exe} {string.Join(' ', Calls[i].Args)}";

    public bool Any(string fragment) =>
        Calls.Any(c => $"{c.Exe} {string.Join(' ', c.Args)}".Contains(fragment));
}
