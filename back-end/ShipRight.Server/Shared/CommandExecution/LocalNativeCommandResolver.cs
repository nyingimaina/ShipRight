namespace ShipRight.Shared.CommandExecution;

/// <summary>
/// Fallback / default strategy: run the executable exactly as given on the local
/// machine. On native Linux this is how `aws` (from apt, snap or pip) runs; the
/// process environment's PATH handles resolution.
/// </summary>
public sealed class LocalNativeCommandResolver : ICommandResolver
{
    public string Name => "local-native";

    public bool Supports(ExecutionTarget target, string executable) => true;

    public Task<ResolvedCommand> ResolveAsync(
        ExecutionTarget target, string executable, string[] args,
        IReadOnlyDictionary<string, string>? envOverride, string? workingDir) =>
        Task.FromResult(new ResolvedCommand
        {
            Transport = CommandTransport.Local,
            Executable = executable,
            Args = args,
            WorkingDir = workingDir,
            EnvOverride = envOverride,
        });
}