namespace ShipRight.Shared.Store;

public static class DataDirectory
{
    public static string Resolve()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".shipright"
        );
        Directory.CreateDirectory(Path.Combine(dir, "builds"));
        Directory.CreateDirectory(Path.Combine(dir, "logs"));
        Directory.CreateDirectory(Path.Combine(dir, "backups"));
        Directory.CreateDirectory(Path.Combine(dir, "scheduler"));
        Directory.CreateDirectory(Path.Combine(dir, "scheduler", "overflow"));
        return dir;
    }
}
