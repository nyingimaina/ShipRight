using System.Reflection;
using System.Text.Json;
using ShipRight.Modules.Builds;
using ShipRight.Modules.Projects;
using ShipRight.Shared.Store;

namespace ShipRight.Modules.System;

public enum AppMode { Desktop, Cloud }

public record HealthPayload(
    string Status,
    string ServerVersion,
    string WebVersion,
    DateTime StartedAt,
    int Port,
    string DataDirectory,
    int ProjectCount,
    int BuildCount,
    AppMode Mode);

public static class HealthRouter
{
    private static readonly DateTime StartedAt = DateTime.UtcNow;
    private static readonly string? ServerVersion = StripGitHash(Assembly.GetEntryAssembly()?
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
        .InformationalVersion);
    private static readonly string? WebVersion = ResolveWebVersion();

    private static string? ResolveWebVersion()
    {
        var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        var versionFile = Path.Combine(wwwroot, "version.json");
        if (File.Exists(versionFile))
        {
            try
            {
                var doc = JsonDocument.Parse(File.ReadAllBytes(versionFile));
                if (doc.RootElement.TryGetProperty("version", out var v))
                    return v.GetString();
            }
            catch { }
        }
        return null;
    }

    private static string? StripGitHash(string? version)
    {
        if (version == null) return null;
        var plusIdx = version.IndexOf('+');
        return plusIdx >= 0 ? version[..plusIdx] : version;
    }

    public static void MapHealthRoutes(this WebApplication app)
        => MapHealthRoutes(app, AppMode.Desktop);

    public static void MapHealthRoutes(this WebApplication app, AppMode mode)
    {
        app.MapGet("/api/health", (IProjectStore projectStore, IBuildStore buildStore) =>
            Results.Ok(BuildHealthPayload(
                mode,
                ServerVersion ?? "0.0.0",
                WebVersion ?? "0.0.0",
                DataDirectory.Resolve(),
                projectStore.Count,
                buildStore.Count,
                StartedAt)));
    }

    public static HealthPayload BuildHealthPayload(
        AppMode mode,
        string serverVersion,
        string webVersion,
        string dataDirectory,
        int projectCount,
        int buildCount,
        DateTime startedAt) => new(
        "healthy",
        serverVersion,
        webVersion,
        startedAt,
        5200,
        dataDirectory,
        projectCount,
        buildCount,
        mode);
}
