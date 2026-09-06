using System.Text;

namespace ShipRight.Shared.CommandExecution;

/// <summary>
/// Remote strategy: packs (exe, args, env) into a single login-shell one-liner
/// (`env VAR='..' '/abs/exe' 'arg' ...`) wrapped in `bash -lc`, so the remote
/// host's login PATH (apt, snap, pip) is used.
/// </summary>
public sealed class SshCommandResolver : ICommandResolver
{
    public string Name => "ssh";

    public bool Supports(ExecutionTarget target, string executable) => target.IsRemote;

    public Task<ResolvedCommand> ResolveAsync(
        ExecutionTarget target, string executable, string[] args,
        IReadOnlyDictionary<string, string>? envOverride, string? workingDir)
    {
        var inner = BuildOneLiner(executable, args, envOverride);
        var wrapper = "bash -lc " + SingleQuote(inner);

        return Task.FromResult(new ResolvedCommand
        {
            Transport = CommandTransport.Ssh,
            Executable = executable,
            SshCommand = wrapper,
            SshHost = target.Host,
            SshUsername = target.Username,
            SshKeyPath = target.KeyPath,
        });
    }

    private static string BuildOneLiner(
        string executable, string[] args, IReadOnlyDictionary<string, string>? envOverride)
    {
        var sb = new StringBuilder();

        if (envOverride is { Count: > 0 })
        {
            foreach (var (key, value) in envOverride)
                sb.Append(key).Append('=').Append(SingleQuote(value)).Append(' ');
        }

        sb.Append(SingleQuote(executable));
        foreach (var arg in args)
            sb.Append(' ').Append(SingleQuote(arg));

        return sb.ToString();
    }

    private static string SingleQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";
}