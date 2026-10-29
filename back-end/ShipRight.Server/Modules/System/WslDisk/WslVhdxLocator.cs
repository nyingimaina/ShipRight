using Microsoft.Win32;

namespace ShipRight.Modules.System.WslDisk;

/// <summary>
/// Finds the virtual disk files of WSL distros wherever Windows put them. <see cref="WslDiskService"/> only looks in
/// the Store packages folder, which is right for a distro installed from the Store but misses one that was moved,
/// imported to another drive, or installed without a Store package. WSL itself records where each distro lives in the
/// registry, so that is asked first, plus the folder newer WSL installs use. This only adds places to look; a disk
/// the Store folder already finds is unaffected.
/// </summary>
public sealed class WslVhdxLocator
{
    private const string LxssKey = @"Software\Microsoft\Windows\CurrentVersion\Lxss";

    private readonly Func<IEnumerable<(string BasePath, string? VhdFileName)>> _registered;
    private readonly IReadOnlyList<string> _folderRoots;

    public WslVhdxLocator(
        Func<IEnumerable<(string BasePath, string? VhdFileName)>>? registered = null,
        IEnumerable<string>? folderRoots = null)
    {
        _registered = registered ?? ReadRegistry;
        _folderRoots = (folderRoots ?? DefaultFolderRoots()).ToList();
    }

    public IReadOnlyList<string> Find()
    {
        var found = new List<string>();
        void Add(string path)
        {
            if (!found.Contains(path, StringComparer.OrdinalIgnoreCase) && File.Exists(path)) found.Add(path);
        }

        try
        {
            foreach (var (basePath, fileName) in _registered())
            {
                if (string.IsNullOrWhiteSpace(basePath)) continue;
                var folder = basePath.StartsWith(@"\\?\", StringComparison.Ordinal) ? basePath[4..] : basePath;
                Add(Path.Combine(folder, string.IsNullOrWhiteSpace(fileName) ? "ext4.vhdx" : fileName));
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or global::System.Security.SecurityException) { }

        foreach (var root in _folderRoots)
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                foreach (var dir in Directory.GetDirectories(root))
                    foreach (var file in Directory.GetFiles(dir, "*.vhdx"))
                        Add(file);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { }
        }
        return found;
    }

    /// <summary>Where it looked, for a message that says more than "not found".</summary>
    public IReadOnlyList<string> SearchedPlaces()
    {
        var places = new List<string>();
        try { places.AddRange(_registered().Select(r => r.BasePath).Where(p => !string.IsNullOrWhiteSpace(p))); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or global::System.Security.SecurityException) { }
        places.AddRange(_folderRoots);
        return places;
    }

    private static IEnumerable<(string BasePath, string? VhdFileName)> ReadRegistry()
    {
        if (!OperatingSystem.IsWindows()) yield break;

        // The signed-in user first. A copy of ShipRight started as another administrator has a different current user,
        // so the other loaded user profiles are read too.
        var hives = new List<RegistryKey> { Registry.CurrentUser };
        foreach (var sid in Registry.Users.GetSubKeyNames().Where(n => !n.EndsWith("_Classes", StringComparison.OrdinalIgnoreCase)))
        {
            RegistryKey? hive = null;
            try { hive = Registry.Users.OpenSubKey(sid); } catch (Exception ex) when (ex is UnauthorizedAccessException or global::System.Security.SecurityException) { }
            if (hive is not null) hives.Add(hive);
        }

        foreach (var hive in hives)
        {
            using var lxss = hive.OpenSubKey(LxssKey);
            if (lxss is null) continue;
            foreach (var name in lxss.GetSubKeyNames())
            {
                using var distro = lxss.OpenSubKey(name);
                if (distro?.GetValue("BasePath") is string basePath)
                    yield return (basePath, distro.GetValue("VhdFileName") as string);
            }
        }
    }

    private static IEnumerable<string> DefaultFolderRoots()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(local)) yield return Path.Combine(local, "wsl");
    }
}
