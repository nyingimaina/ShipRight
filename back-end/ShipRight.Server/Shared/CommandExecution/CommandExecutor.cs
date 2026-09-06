using System.Diagnostics;
using System.Text;
using ShipRight.Shared.ProcessRunner;
using ShipRight.Shared.SshRunner;

namespace ShipRight.Shared.CommandExecution;

/// <summary>
/// Dispatches commands through the resolver registry: local targets run via
/// <see cref="IProcessRunner"/> (native or WSL-wrapped), remote targets run via
/// <see cref="ISshRunner"/> with the resolver-built one-liner, buffering output
/// into a <see cref="ProcessResult"/>.
/// </summary>
public sealed class CommandExecutor : ICommandExecutor
{
    private readonly IExecutionTargetProvider _targetProvider;
    private readonly CommandResolverRegistry _registry;
    private readonly IProcessRunner _runner;
    private readonly ISshRunner? _ssh;

    public CommandExecutor(
        IExecutionTargetProvider targetProvider,
        CommandResolverRegistry registry,
        IProcessRunner runner,
        ISshRunner? ssh = null)
    {
        _targetProvider = targetProvider;
        _registry = registry;
        _runner = runner;
        _ssh = ssh;
    }

    /// <summary>A local-only executor that runs every command exactly as given (no WSL/SSH resolution).</summary>
    public static CommandExecutor PassthroughLocal(IProcessRunner runner) =>
        new(new ExecutionTargetProvider(), CommandResolverRegistry.PassthroughLocal(), runner);

    public async Task<ProcessResult> RunAsync(
        string executable,
        string[] args,
        string? workingDir,
        Func<string, Task>? onOutput = null,
        Func<string, Task>? onError = null,
        CancellationToken ct = default,
        IReadOnlyDictionary<string, string>? envOverride = null,
        TimeSpan? timeout = null,
        string? stdin = null)
    {
        var target = _targetProvider.GetTarget();
        var command = await _registry.ResolveAsync(target, executable, args, envOverride, workingDir);

        if (command.Transport == CommandTransport.Local)
            return await _runner.RunAsync(
                command.Executable, command.Args, command.WorkingDir,
                onOutput, onError, ct, command.EnvOverride, timeout, stdin);

        return await RunOverSshAsync(command, onOutput, onError, ct, timeout);
    }

    private async Task<ProcessResult> RunOverSshAsync(
        ResolvedCommand command,
        Func<string, Task>? onOutput,
        Func<string, Task>? onError,
        CancellationToken ct,
        TimeSpan? timeout)
    {
        if (_ssh is null || string.IsNullOrEmpty(command.SshCommand) || string.IsNullOrEmpty(command.SshHost))
            throw new InvalidOperationException(
                "An SSH execution target is configured but no SSH runner is available.");

        var sw = Stopwatch.StartNew();
        var stdoutBuf = new StringBuilder();
        var stderrBuf = new StringBuilder();

        async Task BufferStdout(string line)
        {
            stdoutBuf.Append(line).Append('\n');
            if (onOutput is not null)
                try { await onOutput(line); } catch { }
        }

        async Task BufferStderr(string line)
        {
            stderrBuf.Append(line).Append('\n');
            if (onError is not null)
                try { await onError(line); } catch { }
            else if (onOutput is not null)
                try { await onOutput(line); } catch { }
        }

        var exitCode = await _ssh.RunAsync(
            command.SshHost,
            command.SshUsername ?? "root",
            command.SshKeyPath ?? string.Empty,
            command.SshCommand,
            BufferStdout,
            BufferStderr,
            ct);

        return new ProcessResult(exitCode, stdoutBuf.ToString(), stderrBuf.ToString(), sw.Elapsed);
    }
}