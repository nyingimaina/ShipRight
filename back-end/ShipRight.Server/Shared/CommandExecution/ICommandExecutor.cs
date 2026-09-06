using ShipRight.Shared.ProcessRunner;

namespace ShipRight.Shared.CommandExecution;

/// <summary>
/// Runs a command against whatever execution target is currently configured.
/// Mirrors the IProcessRunner signature so call sites stay identical.
/// </summary>
public interface ICommandExecutor
{
    Task<ProcessResult> RunAsync(
        string executable,
        string[] args,
        string? workingDir,
        Func<string, Task>? onOutput = null,
        Func<string, Task>? onError = null,
        CancellationToken ct = default,
        IReadOnlyDictionary<string, string>? envOverride = null,
        TimeSpan? timeout = null,
        string? stdin = null);
}