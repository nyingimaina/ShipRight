using System.Text.Json;

namespace ShipRight.Modules.JitDeploy;

public sealed class ComposeConfigException(string detail) : Exception($"The compose file could not be understood: {detail}");

public sealed record ComposeService(string Name, IReadOnlyList<string> DependsOn, bool HasBuild, string? Image);

/// <summary>What the deploy needs a buildable service to look like once compose has rendered it.</summary>
public sealed record BuildExpectation(string Service, string TagVariable, string ExpectedImage);

/// <summary>Reads the output of <c>docker compose config --format json</c>.</summary>
public static class ComposeConfig
{
    public static IReadOnlyList<ComposeService> Parse(string json)
    {
        var start = (json ?? string.Empty).IndexOf('{');
        if (start < 0) throw new ComposeConfigException("there is no configuration in the output.");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json![start..]); }
        catch (JsonException ex) { throw new ComposeConfigException(ex.Message); }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("services", out var services)
                || services.ValueKind != JsonValueKind.Object)
                throw new ComposeConfigException("it has no services.");

            var result = new List<ComposeService>();
            foreach (var s in services.EnumerateObject())
            {
                var deps = new List<string>();
                if (s.Value.TryGetProperty("depends_on", out var d))
                {
                    if (d.ValueKind == JsonValueKind.Object) deps.AddRange(d.EnumerateObject().Select(p => p.Name));
                    else if (d.ValueKind == JsonValueKind.Array) deps.AddRange(d.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!));
                }
                var hasBuild = s.Value.TryGetProperty("build", out var b) && b.ValueKind != JsonValueKind.Null;
                string? image = s.Value.TryGetProperty("image", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() : null;
                result.Add(new ComposeService(s.Name, deps, hasBuild, image));
            }
            if (result.Count == 0) throw new ComposeConfigException("it has no services.");
            return result;
        }
    }

    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Graph(IEnumerable<ComposeService> services) =>
        services.ToDictionary(s => s.Name, s => s.DependsOn, StringComparer.Ordinal);

    /// <summary>
    /// Plain-language problems that would stop a release being built and told apart: the service is missing, has
    /// nothing to build, or its image line does not carry the release tag. Empty when everything is fine.
    /// </summary>
    public static IReadOnlyList<string> CheckBuilds(IEnumerable<ComposeService> services, IEnumerable<BuildExpectation> expectations)
    {
        var byName = services.ToDictionary(s => s.Name, StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (var e in expectations)
        {
            if (!byName.TryGetValue(e.Service, out var svc))
            {
                problems.Add($"Service '{e.Service}' is not in the compose file, so there is nothing to build for it.");
                continue;
            }
            if (!svc.HasBuild)
            {
                problems.Add($"Service '{e.Service}' has no build: section, so it cannot be built on the server.");
                continue;
            }
            if (!string.Equals(svc.Image, e.ExpectedImage, StringComparison.Ordinal))
            {
                problems.Add($"Service '{e.Service}' builds an image, but its image: line is '{svc.Image ?? "(not set)"}'. " +
                             $"Write it as image: ${{{e.TagVariable}:-{WithoutTag(e.ExpectedImage)}:latest}} so every release gets its own tag.");
            }
        }
        return problems;
    }

    private static string WithoutTag(string image)
    {
        var colon = image.LastIndexOf(':');
        return colon > image.LastIndexOf('/') ? image[..colon] : image;
    }
}
