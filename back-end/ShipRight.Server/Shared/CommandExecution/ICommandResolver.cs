namespace ShipRight.Shared.CommandExecution;

/// <summary>
/// Strategy that decides how a (executable, args) pair runs for a given target:
/// native Linux, WSL-wrapped on Windows, or as a command over SSH.
/// </summary>
public interface ICommandResolver
{
    string Name { get; }

    bool Supports(ExecutionTarget target, string executable);

    Task<ResolvedCommand> ResolveAsync(
        ExecutionTarget target, string executable, string[] args,
        IReadOnlyDictionary<string, string>? envOverride, string? workingDir);
}