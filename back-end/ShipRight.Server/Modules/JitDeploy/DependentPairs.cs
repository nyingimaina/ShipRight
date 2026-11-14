namespace ShipRight.Modules.JitDeploy;

/// <summary>"When <see cref="Service"/> is replaced, restart <see cref="Dependent"/>?" and the current answer.</summary>
public sealed record DependentPair(string Service, string Dependent, bool Restart);

/// <summary>
/// Works out which restart questions a person has to be asked. A dependent the deploy builds itself is replaced
/// anyway, so it is never asked about. For the others there is a question per (service, dependent) pair, and a
/// "yes" opens the next question down the chain (the dependent's own dependents); a "no" ends that branch.
/// Unanswered pairs count as "restart", exactly as <see cref="AffectedSet"/> treats them.
/// </summary>
public static class DependentPairs
{
    public static IReadOnlyList<DependentPair> Compute(
        IReadOnlyDictionary<string, IReadOnlyList<string>> dependsOn,
        IEnumerable<string> built,
        IReadOnlyDictionary<(string Service, string Dependent), bool>? choices)
    {
        var builtSet = built.ToHashSet(StringComparer.Ordinal);

        var dependentsOf = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (service, deps) in dependsOn)
            foreach (var d in deps.Distinct(StringComparer.Ordinal).Where(dependsOn.ContainsKey))
            {
                if (!dependentsOf.TryGetValue(d, out var list)) dependentsOf[d] = list = [];
                list.Add(service);
            }

        var pairs = new List<DependentPair>();
        var visited = new HashSet<string>(builtSet.Where(dependsOn.ContainsKey), StringComparer.Ordinal);
        var queue = new Queue<string>(visited.OrderBy(v => v, StringComparer.Ordinal));

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!dependentsOf.TryGetValue(current, out var dependents)) continue;
            foreach (var dependent in dependents.OrderBy(d => d, StringComparer.Ordinal))
            {
                if (builtSet.Contains(dependent)) continue;
                var restart = choices is null || !choices.TryGetValue((current, dependent), out var c) || c;
                pairs.Add(new DependentPair(current, dependent, restart));
                if (restart && visited.Add(dependent)) queue.Enqueue(dependent);
            }
        }

        return pairs.OrderBy(p => p.Service, StringComparer.Ordinal).ThenBy(p => p.Dependent, StringComparer.Ordinal).ToList();
    }
}
