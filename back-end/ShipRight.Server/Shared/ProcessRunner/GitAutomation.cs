namespace ShipRight.Shared.ProcessRunner;

/// <summary>
/// Git subcommands that contact a remote. In an automated pipeline they must never open a
/// credential prompt: the process has no console, so git blocks on stdin forever and the build
/// hangs with no log output (a Windows-side GCM dialog or a WSL-side terminal prompt).
/// </summary>
public static class GitAutomation
{
    // Generous enough for a large push, short enough that a dead connection surfaces as a
    // failed build instead of an indefinite hang.
    public static readonly TimeSpan DefaultNetworkTimeout = TimeSpan.FromMinutes(15);

    private static readonly HashSet<string> _networkSubcommands =
        ["clone", "fetch", "pull", "push", "ls-remote", "submodule"];

    private static readonly HashSet<string> _globalFlagsWithValue =
        [
            "-C", "-c", "--git-dir", "--work-tree", "--namespace", "--exec-path",
            "--super-prefix", "--config-env"
        ];

    public static IReadOnlyDictionary<string, string> NonInteractiveEnvironment { get; } =
        new Dictionary<string, string>
        {
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["GCM_INTERACTIVE"] = "Never",
        };

    public static bool RequiresNetwork(string executable, IReadOnlyList<string> args)
    {
        if (executable != "git") return false;
        return FindSubcommand(args) is { } subcommand && _networkSubcommands.Contains(subcommand);
    }

    public static IReadOnlyDictionary<string, string> MergeEnvironment(
        IReadOnlyDictionary<string, string>? callerOverride)
    {
        var merged = new Dictionary<string, string>(NonInteractiveEnvironment);
        if (callerOverride is null) return merged;

        foreach (var (key, value) in callerOverride) merged[key] = value;
        return merged;
    }

    public static TimeSpan ResolveTimeout(TimeSpan? callerTimeout) =>
        callerTimeout ?? DefaultNetworkTimeout;

    public static GitDefaults Apply(
        string executable,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string>? envOverride,
        TimeSpan? timeout) =>
        RequiresNetwork(executable, args)
            ? new GitDefaults(MergeEnvironment(envOverride), ResolveTimeout(timeout))
            : new GitDefaults(envOverride, timeout);

    /// <summary>
    /// Git accepts global flags before the subcommand; the first bare word is the subcommand.
    /// Boolean flags are skipped, flags that take a value consume the following argument.
    /// </summary>
    private static string? FindSubcommand(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg.Length == 0) continue;
            if (!arg.StartsWith('-')) return arg;
            if (_globalFlagsWithValue.Contains(arg)) i++;
        }
        return null;
    }
}

public sealed record GitDefaults(IReadOnlyDictionary<string, string>? Env, TimeSpan? Timeout);
