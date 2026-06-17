using System.Text.RegularExpressions;
using Serilog;
using ShipRight.Modules.Projects;

namespace ShipRight.Modules.VersionFiles;

public record ServiceCurrentVersion(
    string ServiceName,
    string Version,
    string VersionFilePath);

public static class VersionFileService
{
    private static readonly Regex SemVer = new(@"^\d+\.\d+\.\d+$", RegexOptions.Compiled);

    public static async Task<ServiceCurrentVersion> ReadAsync(ServiceConfig service)
    {
        var raw = (await File.ReadAllTextAsync(service.VersionFilePath)).Trim();
        if (!SemVer.IsMatch(raw))
            throw new InvalidOperationException(
                $"Invalid version '{raw}' in {service.VersionFilePath}. Expected MAJOR.MINOR.PATCH.");
        return new ServiceCurrentVersion(service.Name, raw, service.VersionFilePath);
    }

    public static string SuggestNext(string current)
    {
        var parts = current.Split('.');
        return $"{parts[0]}.{parts[1]}.{int.Parse(parts[2]) + 1}";
    }

    public static async Task WriteAsync(string path, string version)
    {
        if (!SemVer.IsMatch(version))
            throw new ArgumentException($"Invalid version string: {version}");
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(path, version + "\n");
        Log.Information("Version file written: {Path} → {Version}", path, version);
    }
}
