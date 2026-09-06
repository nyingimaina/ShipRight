namespace ShipRight.Shared.CommandExecution;

/// <summary>
/// Ordered resolver selection, mirroring RegistryAuthProviderRegistry: the first
/// resolver that supports a (target, executable) pair wins; otherwise a built-in
/// local-native passthrough is used.
/// </summary>
public sealed class CommandResolverRegistry
{
    private readonly List<ICommandResolver> _resolvers = [];
    private readonly ICommandResolver _fallback = new LocalNativeCommandResolver();

    public CommandResolverRegistry Register(params ICommandResolver[] resolvers)
    {
        _resolvers.AddRange(resolvers);
        return this;
    }

    public Task<ResolvedCommand> ResolveAsync(
        ExecutionTarget target, string executable, string[] args,
        IReadOnlyDictionary<string, string>? envOverride, string? workingDir) =>
        (_resolvers.FirstOrDefault(r => r.Supports(target, executable)) ?? _fallback)
            .ResolveAsync(target, executable, args, envOverride, workingDir);

    /// <summary>Local-only passthrough: every command runs exactly as given. Used when no executor is injected.</summary>
    public static CommandResolverRegistry PassthroughLocal() => new();

    /// <summary>The production registry: SSH (remote targets) then WSL (Windows local), native as fallback.</summary>
    public static CommandResolverRegistry CreateDefault(IWslToolLocator locator) =>
        new CommandResolverRegistry().Register(new SshCommandResolver(), new WslCommandResolver(locator));
}