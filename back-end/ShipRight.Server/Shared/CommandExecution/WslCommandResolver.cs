namespace ShipRight.Shared.CommandExecution;

/// <summary>
/// Windows-local strategy: known Linux tools run inside WSL. The exe is resolved
/// to its absolute path via <see cref="IWslToolLocator"/> so snap (and pip / apt)
/// installs work even in non-login shells, then runs as `wsl <absPath> <args>`.
/// Falls back to the bare name when the tool cannot be located.
/// </summary>
public sealed class WslCommandResolver : ICommandResolver
{
    private static readonly HashSet<string> ToolNames =
    [
        "docker", "git", "bash", "sh", "python3", "aws",
        "curl", "sudo", "apt", "apt-get", "snap", "pip3", "unzip", "wget",
    ];

    private readonly IWslToolLocator _locator;

    public WslCommandResolver(IWslToolLocator locator)
    {
        _locator = locator;
    }

    public string Name => "wsl";

    public bool Supports(ExecutionTarget target, string executable) =>
        !target.IsRemote && OperatingSystem.IsWindows() && ToolNames.Contains(executable);

    public async Task<ResolvedCommand> ResolveAsync(
        ExecutionTarget target, string executable, string[] args,
        IReadOnlyDictionary<string, string>? envOverride, string? workingDir)
    {
        var resolved = await _locator.LocateAsync(executable) ?? executable;
        var wslArgs = new[] { resolved }
            .Concat(args.Select(ShipRight.Shared.ProcessRunner.ProcessRunner.ToWslPath))
            .ToArray();

        return new ResolvedCommand
        {
            Transport = CommandTransport.Local,
            Executable = "wsl",
            Args = wslArgs,
            WorkingDir = workingDir,
            EnvOverride = envOverride,
        };
    }
}