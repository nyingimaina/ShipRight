using System.Text.RegularExpressions;

namespace ShipRight.Modules.Resources;

/// <summary>
/// Parses the AWS shared-credentials/config INI files without exposing secret values.
/// Only profile names, region and "has keys" presence flags are surfaced — the actual
/// access key / secret values found in the files are deliberately never returned.
/// </summary>
public static partial class AwsCredentialsParser
{
    private static readonly string[] _ssoKeys = ["sso_start_url", "sso_account_id", "sso_role_name", "sso_region"];

    public static IReadOnlyList<AwsCredentialProfile> Parse(string? credentialsText, string? configText)
    {
        var credentials = ParseSections(credentialsText);
        var config = ParseSections(configText);

        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var key in credentials.Keys) names.Add(key);
        foreach (var key in config.Keys) names.Add(key);

        var profiles = new List<AwsCredentialProfile>(names.Count);
        foreach (var name in names)
        {
            credentials.TryGetValue(name, out var credEntry);
            config.TryGetValue(name, out var configEntry);

            var entries = Merge(credEntry, configEntry);
            if (entries is null || entries.Count == 0) continue;

            var hasKeys = ContainsKey(credEntry, "aws_access_key_id")
                && ContainsKey(credEntry, "aws_secret_access_key");

            var unsupported = DetectUnsupported(entries);
            var region = GetValue(configEntry, "region")
                ?? GetValue(credEntry, "region");

            profiles.Add(new AwsCredentialProfile(name, region, hasKeys, unsupported));
        }

        return profiles;
    }

    private static List<KeyValuePair<string, string>>? Merge(
        List<KeyValuePair<string, string>>? credentials,
        List<KeyValuePair<string, string>>? config)
    {
        if (credentials is null) return config;
        if (config is null) return credentials;

        var merged = new List<KeyValuePair<string, string>>(credentials);
        var seen = new HashSet<string>(credentials.Select(kv => kv.Key));
        foreach (var kv in config)
        {
            if (seen.Add(kv.Key))
                merged.Add(kv);
        }
        return merged;
    }

    private static string? DetectUnsupported(List<KeyValuePair<string, string>>? entries)
    {
        if (entries is null) return null;
        if (entries.Any(kv => kv.Key == "credential_process"))
            return "Uses credential_process (external command) — not injectable as plain env vars.";
        if (entries.Any(kv => _ssoKeys.Contains(kv.Key)))
            return "Uses AWS SSO — not supported with plain env vars. Use named-profile mode with iam-role converted keys.";
        return null;
    }

    private static bool ContainsKey(IEnumerable<KeyValuePair<string, string>>? entries, string key) =>
        entries is not null && entries.Any(kv => kv.Key == key);

    private static string? GetValue(IEnumerable<KeyValuePair<string, string>>? entries, string key) =>
        entries?.FirstOrDefault(kv => kv.Key == key).Value;

    /// <summary>
    /// Parses an INI section map keyed by profile name. In credentials files named
    /// profiles appear as "[name]"; in config files as "[profile name]" — both are
    /// normalized to "name". "[default]" stays "default".
    /// </summary>
    public static Dictionary<string, List<KeyValuePair<string, string>>> ParseSections(string? text)
    {
        var result = new Dictionary<string, List<KeyValuePair<string, string>>>();
        if (string.IsNullOrEmpty(text)) return result;

        string? current = null;
        List<KeyValuePair<string, string>>? entries = null;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('#') || line.StartsWith(';')) continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                current = NormalizeSectionName(line[1..^1].Trim());
                if (current.Length == 0) continue;
                if (!result.TryGetValue(current, out entries))
                {
                    entries = [];
                    result[current] = entries;
                }
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0 || current is null || entries is null) continue;

            var key = line[..eq].Trim().ToLowerInvariant();
            var value = line[(eq + 1)..].Trim();
            entries.Add(new KeyValuePair<string, string>(key, value));
        }

        return result;
    }

    /// <summary>"[default]" → "default"; "[profile prod]" → "prod"; "[prod]" → "prod".</summary>
    public static string NormalizeSectionName(string raw)
    {
        if (raw == "default") return "default";
        if (raw.StartsWith("profile ", StringComparison.OrdinalIgnoreCase))
            return raw["profile ".Length..].Trim();
        return raw;
    }
}