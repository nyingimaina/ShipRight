using System.Text.RegularExpressions;

namespace ShipRight.Modules.Projects;

public record PathChange(
    string ServiceName,
    string OldVersionFilePath,
    string NewVersionFilePath,
    string OldBuildContextPath,
    string NewBuildContextPath);

/// <summary>
/// What a refresh would change. Changes lists matched services whose paths differ.
/// NotFoundInFolder lists project services nothing in the folder matched; NewInFolder lists
/// detected services no project service matched. Neither of those two is ever added or removed.
/// </summary>
public record PathRefreshPlan(
    List<PathChange> Changes,
    List<string> NotFoundInFolder,
    List<string> NewInFolder);

/// <summary>
/// Compares a stored project with a fresh detection and updates ONLY each matched service's
/// VersionFilePath and BuildContextPath. Every other field, including every secret, is carried
/// over untouched, and service names never change (credentials are matched by name elsewhere).
/// </summary>
public static class ProjectPathRefresher
{
    public static PathRefreshPlan Plan(ProjectConfig project, DetectedProjectConfig detected)
    {
        var remaining = detected.Services.ToList();
        var matches = new List<(ServiceConfig Existing, DetectedService Found)>();
        var unmatched = project.Services.ToList();

        MatchBy(unmatched, remaining, matches, (s, d) => SamePath(s.VersionFilePath, d.VersionFilePath));
        MatchBy(unmatched, remaining, matches, (s, d) => SameText(s.DockerImageName, d.DockerImageName));
        MatchBy(unmatched, remaining, matches, (s, d) => SameText(s.ComposeServiceName, d.ComposeServiceName));
        MatchBy(unmatched, remaining, matches, (s, d) => SameName(s.Name, d.SuggestedName));

        var changes = matches
            .Where(m => !SamePath(m.Existing.VersionFilePath, m.Found.VersionFilePath)
                     || !SamePath(m.Existing.BuildContextPath, m.Found.BuildContextPath))
            .Select(m => new PathChange(
                m.Existing.Name,
                m.Existing.VersionFilePath, m.Found.VersionFilePath,
                m.Existing.BuildContextPath, m.Found.BuildContextPath))
            .ToList();

        return new PathRefreshPlan(
            changes,
            unmatched.Select(s => s.Name).ToList(),
            remaining.Select(d => d.SuggestedName).ToList());
    }

    public static ProjectConfig Apply(ProjectConfig project, PathRefreshPlan plan)
    {
        var byName = plan.Changes.ToDictionary(c => c.ServiceName);
        foreach (var name in byName.Keys)
            if (!project.Services.Any(s => s.Name == name))
                throw new InvalidOperationException($"Service '{name}' is not part of project '{project.Id}'.");

        var services = project.Services
            .Select(s => byName.TryGetValue(s.Name, out var c)
                ? s with { VersionFilePath = c.NewVersionFilePath, BuildContextPath = c.NewBuildContextPath }
                : s)
            .ToList();

        return project with { Services = services };
    }

    private static void MatchBy(
        List<ServiceConfig> unmatched,
        List<DetectedService> remaining,
        List<(ServiceConfig, DetectedService)> matches,
        Func<ServiceConfig, DetectedService, bool> isMatch)
    {
        foreach (var service in unmatched.ToList())
        {
            var found = remaining.FirstOrDefault(d => isMatch(service, d));
            if (found is null) continue;
            matches.Add((service, found));
            unmatched.Remove(service);
            remaining.Remove(found);
        }
    }

    private static bool SamePath(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b)
        && string.Equals(NormalisePath(a), NormalisePath(b), StringComparison.OrdinalIgnoreCase);

    private static string NormalisePath(string path) => path.Replace('\\', '/').TrimEnd('/');

    private static bool SameText(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b)
        && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool SameName(string? a, string? b)
    {
        var left = Letters(a);
        return left.Length > 0 && left == Letters(b);
    }

    private static string Letters(string? text) =>
        Regex.Replace(text ?? string.Empty, "[^A-Za-z0-9]", string.Empty).ToLowerInvariant();
}
