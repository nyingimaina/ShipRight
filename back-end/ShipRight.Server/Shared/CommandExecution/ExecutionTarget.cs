namespace ShipRight.Shared.CommandExecution;

/// <summary>
/// Where a command should run. Local targets run through the process runner
/// (native Linux, or WSL-wrapped on Windows); remote targets run over SSH.
/// </summary>
public sealed record ExecutionTarget(string Name, string? Host = null, string? Username = null, string? KeyPath = null)
{
    public bool IsRemote => Host is not null;

    public static ExecutionTarget Local { get; } = new("local");
}