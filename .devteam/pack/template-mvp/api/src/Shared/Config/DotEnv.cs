namespace MyApp.Api.Shared.Config;

/// <summary>Reads a .env file (KEY=value lines) so running the app from a terminal finds the same settings Docker does.</summary>
public static class DotEnv
{
    public static Dictionary<string, string> Parse(string text)
    {
        var values = new Dictionary<string, string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var equals = line.IndexOf('=');
            if (equals <= 0) continue;
            var key = line[..equals].Trim();
            var value = line[(equals + 1)..].Trim();
            if (value.Length >= 2 && (value[0] == '"' && value[^1] == '"' || value[0] == '\'' && value[^1] == '\'')) value = value[1..^1];
            if (value.Length > 0) values[key] = value;
        }
        return values;
    }

    /// <summary>The .env file in this folder or a parent, if there is one.</summary>
    public static string? Find(string startDirectory)
    {
        for (var dir = new DirectoryInfo(startDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, ".env"))) return Path.Combine(dir.FullName, ".env");
        return null;
    }

    /// <summary>
    /// Finds .env in the folder (or, by default, a parent) and sets each value that isn't already set in the environment,
    /// so a real environment variable always wins. Returns whether a file was found.
    /// </summary>
    public static bool Load(string startDirectory, bool searchUp = true)
    {
        for (var dir = new DirectoryInfo(startDirectory); dir is not null; dir = searchUp ? dir.Parent : null)
        {
            var path = Path.Combine(dir.FullName, ".env");
            if (!File.Exists(path)) continue;
            foreach (var (key, value) in Parse(File.ReadAllText(path)))
                if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key))) Environment.SetEnvironmentVariable(key, value);
            return true;
        }
        return false;
    }
}
