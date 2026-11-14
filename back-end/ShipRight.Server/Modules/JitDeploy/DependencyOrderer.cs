namespace ShipRight.Modules.JitDeploy;

public sealed class DependencyCycleException : Exception
{
    public IReadOnlyList<string> Cycle { get; }

    public DependencyCycleException(IReadOnlyList<string> cycle)
        : base($"These services depend on each other in a circle, so there is no safe order to start them in: {string.Join(" -> ", cycle)}")
        => Cycle = cycle;
}

public sealed class UnknownDependencyException : Exception
{
    public string Service { get; }
    public string Dependency { get; }

    public UnknownDependencyException(string service, string dependency)
        : base($"Service '{service}' depends on '{dependency}', which is not defined in the compose file.")
    {
        Service = service;
        Dependency = dependency;
    }
}

/// <summary>
/// Start and stop order for compose services from their <c>depends_on</c> graph. Dependencies start first and stop
/// last. Ties are broken alphabetically so the same graph always gives the same order.
/// </summary>
public static class DependencyOrderer
{
    public static IReadOnlyList<string> StartOrder(IReadOnlyDictionary<string, IReadOnlyList<string>> dependsOn)
    {
        foreach (var (service, deps) in dependsOn)
            foreach (var d in deps)
                if (!dependsOn.ContainsKey(d)) throw new UnknownDependencyException(service, d);

        var remaining = dependsOn.Keys.ToDictionary(k => k, k => dependsOn[k].Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        var order = new List<string>(remaining.Count);

        while (remaining.Count > 0)
        {
            var ready = remaining.Where(kv => kv.Value.Count == 0).Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
            if (ready.Count == 0) throw new DependencyCycleException(FindCycle(remaining));
            // Take one at a time so alphabetical order holds across waves.
            var next = ready[0];
            order.Add(next);
            remaining.Remove(next);
            foreach (var deps in remaining.Values) deps.Remove(next);
        }
        return order;
    }

    public static IReadOnlyList<string> StopOrder(IReadOnlyDictionary<string, IReadOnlyList<string>> dependsOn) =>
        StartOrder(dependsOn).Reverse().ToList();

    /// <summary>Start order restricted to <paramref name="subset"/>, keeping the full graph's relative order.</summary>
    public static IReadOnlyList<string> StartOrder(IReadOnlyDictionary<string, IReadOnlyList<string>> dependsOn, IEnumerable<string> subset)
    {
        var wanted = subset.ToHashSet(StringComparer.Ordinal);
        foreach (var s in wanted)
            if (!dependsOn.ContainsKey(s)) throw new ArgumentException($"Service '{s}' is not defined in the compose file.", nameof(subset));
        return StartOrder(dependsOn).Where(wanted.Contains).ToList();
    }

    public static IReadOnlyList<string> StopOrder(IReadOnlyDictionary<string, IReadOnlyList<string>> dependsOn, IEnumerable<string> subset) =>
        StartOrder(dependsOn, subset).Reverse().ToList();

    /// <summary>Every leftover service still waits on another leftover, so walking those links must loop.</summary>
    private static IReadOnlyList<string> FindCycle(Dictionary<string, HashSet<string>> leftover)
    {
        var path = new List<string>();
        var seenAt = new Dictionary<string, int>(StringComparer.Ordinal);
        var node = leftover.Keys.OrderBy(k => k, StringComparer.Ordinal).First();
        while (!seenAt.ContainsKey(node))
        {
            seenAt[node] = path.Count;
            path.Add(node);
            node = leftover[node].OrderBy(k => k, StringComparer.Ordinal).First();
        }
        var cycle = path.Skip(seenAt[node]).ToList();
        cycle.Add(node);
        return cycle;
    }
}
