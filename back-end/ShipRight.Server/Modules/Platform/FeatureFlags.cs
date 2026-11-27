namespace ShipRight.Modules.Platform;

/// <summary>How specific a flag is. A more specific flag wins over a broader one.</summary>
public enum FlagScope
{
    Global = 0,
    Tenant = 1,
    Company = 2,
    Project = 3,
    User = 4,
}

/// <summary>One flag setting at one scope. The same key may exist at several scopes.</summary>
public sealed record FeatureFlag(
    string Key,
    bool Enabled,
    FlagScope Scope = FlagScope.Global,
    string? ScopeId = null);

/// <summary>Who is asking. Missing values simply do not match scoped flags.</summary>
public sealed record FlagContext(
    string? TenantId = null,
    string? CompanyId = null,
    string? ProjectId = null,
    string? UserId = null);

public interface IFeatureFlagStore
{
    Task<bool> IsEnabledAsync(string key, FlagContext context, CancellationToken ct = default);
    Task<IReadOnlyList<FeatureFlag>> GetAllAsync(CancellationToken ct = default);
    Task SetAsync(FeatureFlag flag, CancellationToken ct = default);
}

/// <summary>
/// The default store: everything is off until someone turns it on, and the most specific matching scope wins. This is
/// the dark-launch switch the spine needs; persistence can be added behind the same contract.
/// </summary>
public sealed class InMemoryFeatureFlagStore : IFeatureFlagStore
{
    private readonly Dictionary<string, List<FeatureFlag>> _flags = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public Task<bool> IsEnabledAsync(string key, FlagContext context, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_flags.TryGetValue(key, out var list)) return Task.FromResult(false);

            var match = list
                .Where(flag => Matches(flag, context))
                .OrderByDescending(flag => (int)flag.Scope)
                .FirstOrDefault();

            return Task.FromResult(match?.Enabled ?? false);
        }
    }

    public Task<IReadOnlyList<FeatureFlag>> GetAllAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            IReadOnlyList<FeatureFlag> all = _flags.Values.SelectMany(list => list).ToList();
            return Task.FromResult(all);
        }
    }

    public Task SetAsync(FeatureFlag flag, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_flags.TryGetValue(flag.Key, out var list))
                _flags[flag.Key] = list = [];

            var index = list.FindIndex(existing =>
                existing.Scope == flag.Scope &&
                string.Equals(existing.ScopeId, flag.ScopeId, StringComparison.Ordinal));
            if (index >= 0) list[index] = flag;
            else list.Add(flag);
        }

        return Task.CompletedTask;
    }

    private static bool Matches(FeatureFlag flag, FlagContext context) => flag.Scope switch
    {
        FlagScope.Global => true,
        FlagScope.Tenant => Same(context.TenantId, flag.ScopeId),
        FlagScope.Company => Same(context.CompanyId, flag.ScopeId),
        FlagScope.Project => Same(context.ProjectId, flag.ScopeId),
        FlagScope.User => Same(context.UserId, flag.ScopeId),
        _ => false,
    };

    private static bool Same(string? value, string? scopeId) =>
        !string.IsNullOrEmpty(value) && string.Equals(value, scopeId, StringComparison.Ordinal);
}
