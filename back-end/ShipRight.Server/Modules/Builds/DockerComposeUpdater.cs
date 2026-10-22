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

    // Matches a default-valued tag variable:  image: "owner/app:${APP_TAG:-1.2.3}"
    // Only the default (1.2.3) is rewritten; the variable stays so a server can override it from .env.
    private static readonly Regex VariableTagLineRegex =
        new(@"^(\s*image:\s*[""']?)([a-z0-9._\-\/]+):(\$\{[A-Za-z_][A-Za-z0-9_]*:-)([a-z0-9._\-]+)(\}[""']?\s*)$",
            RegexOptions.Compiled);

    /// <summary>Like <see cref="UpdateAsync"/>, but also rewrites the default inside <c>${VAR:-tag}</c> tags.</summary>
    public static async Task UpdateTagsAsync(string composePath, IReadOnlyDictionary<string, string> imageToNewVersion)
    {
        // Keep the file's own line endings (and trailing newline) so the git diff is just the tag lines.
        var text = await File.ReadAllTextAsync(composePath);
        var newline = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Split(newline);
        for (int i = 0; i < lines.Length; i++)
        {
            var literal = ImageLineRegex.Match(lines[i]);
            if (literal.Success)
            {
                var name = literal.Groups[2].Value;
                if (imageToNewVersion.TryGetValue(name, out var v))
                    lines[i] = $"{literal.Groups[1].Value}{name}:{v}{literal.Groups[4].Value}";
                continue;
            }

            var variable = VariableTagLineRegex.Match(lines[i]);
            if (!variable.Success) continue;
            var imageName = variable.Groups[2].Value;
            if (imageToNewVersion.TryGetValue(imageName, out var newVersion))
                lines[i] = $"{variable.Groups[1].Value}{imageName}:{variable.Groups[3].Value}{newVersion}{variable.Groups[5].Value}";
        }
        await File.WriteAllTextAsync(composePath, string.Join(newline, lines));
    }

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
