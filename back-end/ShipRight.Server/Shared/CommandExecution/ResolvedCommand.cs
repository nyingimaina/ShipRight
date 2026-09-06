namespace ShipRight.Shared.CommandExecution;

/// <summary>
/// The outcome of resolving a command against an execution target.
/// For <see cref="CommandTransport.Local"/> the executor forwards Executable/Args
/// (which may already be WSL-wrapped, e.g. executable "wsl" with an absolute path).
/// For <see cref="CommandTransport.Ssh"/> the command is baked into a single
/// login-shell one-liner plus the connection details.
/// </summary>
public sealed class ResolvedCommand
{
    public CommandTransport Transport { get; init; } = CommandTransport.Local;
    public string Executable { get; init; } = string.Empty;
    public string[] Args { get; init; } = [];
    public string? WorkingDir { get; init; }
    public IReadOnlyDictionary<string, string>? EnvOverride { get; init; }

    public string? SshCommand { get; init; }
    public string? SshHost { get; init; }
    public string? SshUsername { get; init; }
    public string? SshKeyPath { get; init; }
}