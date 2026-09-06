namespace ShipRight.Shared.Store;

using ShipRight.RuntimeConfig;

public static class DataDirectory
{
    /// <summary>Resolves the legacy (empty-profile) data directory, ensuring subdirectories exist.</summary>
    public static string Resolve()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".shipright"
        );
        EnsureSubdirectories(dir);
        return dir;
    }

    /// <summary>Resolves the data directory for the active profile, ensuring subdirectories exist.</summary>
    public static string Resolve(AppProfile profile)
    {
        EnsureSubdirectories(profile.DataDirectory);
        return profile.DataDirectory;
    }

    private static void EnsureSubdirectories(string dir)
    {
        Directory.CreateDirectory(Path.Combine(dir, "builds"));
        Directory.CreateDirectory(Path.Combine(dir, "logs"));
        Directory.CreateDirectory(Path.Combine(dir, "backups"));
        Directory.CreateDirectory(Path.Combine(dir, "scheduler"));
        Directory.CreateDirectory(Path.Combine(dir, "scheduler", "overflow"));
    }
}
