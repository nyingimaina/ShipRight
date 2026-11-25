using ShipRight.Shared.Store;

namespace ShipRight.Modules.Database;

/// <summary>
/// Where backup artifacts are kept. The root can be pointed at a durable host mount with
/// <c>SHIPRIGHT__BACKUP_DIR</c>; the default stays inside the app data directory so desktop and older
/// deployments keep working unchanged.
/// </summary>
public sealed class BackupStorage
{
    public const string DirectoryVariable = "SHIPRIGHT__BACKUP_DIR";

    private readonly string? _dataDir;

    private BackupStorage(string root, string? dataDir)
    {
        Root = root;
        _dataDir = dataDir is null ? null : Path.GetFullPath(dataDir);
    }

    public string Root { get; }

    public static BackupStorage At(string root) => new(Path.GetFullPath(root), null);

    public static BackupStorage Resolve(string? dataDir = null, Func<string, string?>? getEnv = null)
    {
        var env = getEnv ?? Environment.GetEnvironmentVariable;
        var configured = env(DirectoryVariable);
        if (string.IsNullOrWhiteSpace(configured))
            configured = env("SHIPRIGHT_BACKUP_DIR");

        var resolvedData = dataDir ?? DataDirectory.Resolve();
        var root = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(resolvedData, "backups")
            : configured.Trim();

        return new BackupStorage(Path.GetFullPath(root), resolvedData);
    }

    public string ProjectDirectory(string projectId)
    {
        var dir = Path.Combine(Root, projectId);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public string ArtifactPath(string projectId, string fileName) =>
        Path.Combine(ProjectDirectory(projectId), fileName);

    public string MetadataPath(string projectId, string fileName) =>
        ArtifactPath(projectId, fileName) + ".meta.json";

    /// <summary>Resolves a bare file name inside one project's folder; anything with a path separator is rejected.</summary>
    public string? ResolveArtifact(string projectId, string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        if (fileName.IndexOfAny(['/', '\\']) >= 0 || fileName.Contains("..", StringComparison.Ordinal)) return null;

        var dir = Path.GetFullPath(Path.Combine(Root, projectId));
        var full = Path.GetFullPath(Path.Combine(dir, fileName));
        if (!full.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
        return File.Exists(full) ? full : null;
    }

    /// <summary>Free bytes on the volume that holds the backup root, or -1 when the platform cannot say.</summary>
    public long AvailableBytes()
    {
        try
        {
            var path = Directory.Exists(Root) ? Root : Path.GetDirectoryName(Root);
            if (string.IsNullOrEmpty(path)) return -1;
            var driveRoot = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(driveRoot)) return -1;
            return new DriveInfo(driveRoot).AvailableFreeSpace;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>True when the root lives inside the app data directory (so a data reset would erase it).</summary>
    public bool IsInsideDataDirectory()
    {
        if (_dataDir is null) return false;
        return Root.StartsWith(_dataDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
