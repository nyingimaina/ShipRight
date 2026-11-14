namespace ShipRight.Modules.JitDeploy;

public sealed record AffectedService(string Name, string Reason);

/// <summary>
/// Which services a deploy has to stop and start: the ones whose images were replaced, plus dependents the user chose
/// to restart with them. A dependent is restarted unless the user answered "leave running" for that exact
/// (service, dependent) pair; the choice is applied transitively.
/// </summary>
public static class AffectedSet
{
    public static IReadOnlyList<AffectedService> Compute(
        IReadOnlyDictionary<string, IReadOnlyList<string>> dependsOn,
        IEnumerable<string> replaced,
        IReadOnlyDictionary<(string Service, string Dependent), bool>? choices = null)
    {
        var order = DependencyOrderer.StartOrder(dependsOn);   // also rejects cycles and unknown dependencies

        var replacedList = replaced.Distinct(StringComparer.Ordinal).OrderBy(r => r, StringComparer.Ordinal).ToList();
        foreach (var r in replacedList)
            if (!dependsOn.ContainsKey(r)) throw new ArgumentException($"Service '{r}' is not defined in the compose file.", nameof(replaced));

        var dependentsOf = dependsOn.Keys.ToDictionary(k => k, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var (service, deps) in dependsOn)
            foreach (var d in deps.Distinct(StringComparer.Ordinal)) dependentsOf[d].Add(service);

        var reasons = new Dictionary<string, string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        foreach (var r in replacedList) { reasons[r] = "its image was replaced"; queue.Enqueue(r); }

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var dependent in dependentsOf[current].OrderBy(d => d, StringComparer.Ordinal))
            {
                if (reasons.ContainsKey(dependent)) continue;
                var restart = choices is null || !choices.TryGetValue((current, dependent), out var c) || c;
                if (!restart) continue;
                reasons[dependent] = $"it depends on {current}, which is being restarted";
                queue.Enqueue(dependent);
            }
        }

        return order.Where(reasons.ContainsKey).Select(n => new AffectedService(n, reasons[n])).ToList();
    }
}
