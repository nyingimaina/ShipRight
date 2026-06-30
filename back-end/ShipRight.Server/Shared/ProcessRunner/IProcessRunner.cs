namespace ShipRight.Shared.ProcessRunner;

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(
        string executable,
        string[] args,
        string? workingDir,
        Func<string, Task>? onOutput = null,
        Func<string, Task>? onError = null,
        CancellationToken ct = default,
        IReadOnlyDictionary<string, string>? envOverride = null);
}
