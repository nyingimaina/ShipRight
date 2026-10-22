using System.Text.RegularExpressions;

namespace ShipRight.Modules.RemoteHost;

/// <summary>
/// Static check of the server's env text for values that would stop ShipRight from starting. It reports key names only,
/// never values, and only warns: Prepare must not fail on it.
/// </summary>
public static class RemoteEnvLint
{
    private static readonly string[] Required = ["MYSQL_ROOT_PASSWORD", "MYSQL_PASSWORD", "SHIPRIGHT__DB_CONNECTION"];
    private static readonly string[] NoLocalhost = ["SHIPRIGHT__CORS_ORIGINS", "SHIPRIGHT__PUBLIC_URL"];

    private static Dictionary<string, string> Parse(string? env)
    {
        var map = new Dictionary<string, string>();
        foreach (var raw in (env ?? "").Replace("\r", "").Split('\n'))
        {
            var m = Regex.Match(raw, @"^\s*(?:export\s+)?([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*)$");
            if (!m.Success) continue;
            map[m.Groups[1].Value] = m.Groups[2].Value.Trim().Trim('"', '\'');   // last assignment wins
        }
        return map;
    }

    public static IReadOnlyList<string> Warnings(string? envFile)
    {
        var env = Parse(envFile);
        var found = new List<string>();
        foreach (var key in Required)
            if (!env.TryGetValue(key, out var v) || string.IsNullOrWhiteSpace(v))
                found.Add($"{key} is missing or empty, so the database or ShipRight will not start.");
        foreach (var key in NoLocalhost)
            if (env.TryGetValue(key, out var v) &&
                Regex.IsMatch(v, @"localhost|127\.0\.0\.1|0\.0\.0\.0", RegexOptions.IgnoreCase))
                found.Add($"{key} contains localhost, so ShipRight will not start in cloud mode. Use your https:// address, or remove the line to use SHIPRIGHT_DOMAIN.");
        return found;
    }

    /// <summary>Always exits 0.</summary>
    public static string BuildCommand(string? envFile)
    {
        var warnings = Warnings(envFile);
        if (warnings.Count == 0) return "echo '[settings] OK: no problems found in the environment settings.'; true";
        return string.Join("; ", warnings.Select(w => $"echo '[settings] WARNING: {w.Replace("'", " ")}'")) + "; true";
    }
}
