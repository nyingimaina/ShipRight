using System.Reflection;
using System.Text.Json;
using ShipRight.Modules.Builds;
using ShipRight.Modules.Projects;
using ShipRight.Shared.Store;

namespace ShipRight.Modules.System;

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
    {
        app.MapGet("/api/health", (IProjectStore projectStore, IBuildStore buildStore) =>
            Results.Ok(new
            {
                status = "healthy",
                serverVersion = ServerVersion ?? "0.0.0",
                webVersion = WebVersion ?? "0.0.0",
                startedAt = StartedAt,
                port = 5200,
                dataDirectory = DataDirectory.Resolve(),
                projectCount = projectStore.Count,
                buildCount = buildStore.Count
            }));
    }
}
