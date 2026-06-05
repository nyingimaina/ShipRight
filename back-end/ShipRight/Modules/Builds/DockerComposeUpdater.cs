using System.Text.RegularExpressions;

namespace ShipRight.Modules.Builds;

/// <summary>
/// Updates image tags in docker-compose.yml for specific services without touching other services.
/// Uses line-by-line regex rather than a full YAML parser to avoid a YAML library dependency
/// while still being safe: only replaces lines that match the exact image name pattern.
/// </summary>
public static class DockerComposeUpdater
{
    // Matches:  image: "nyingi/jattac-sms:0.1.4"  or  image: nyingi/jattac-sms:0.1.4
    private static readonly Regex ImageLineRegex =
        new(@"^(\s*image:\s*[""']?)([a-z0-9._\-\/]+):([a-z0-9._\-]+)([""']?\s*)$",
            RegexOptions.Compiled);

    public static async Task UpdateAsync(string composePath, IReadOnlyDictionary<string, string> imageToNewVersion)
    {
        var lines = await File.ReadAllLinesAsync(composePath);
        for (int i = 0; i < lines.Length; i++)
        {
            var m = ImageLineRegex.Match(lines[i]);
            if (!m.Success) continue;

            var imageName = m.Groups[2].Value;
            if (imageToNewVersion.TryGetValue(imageName, out var newVersion))
                lines[i] = $"{m.Groups[1].Value}{imageName}:{newVersion}{m.Groups[4].Value}";
        }
        await File.WriteAllLinesAsync(composePath, lines);
    }
}
