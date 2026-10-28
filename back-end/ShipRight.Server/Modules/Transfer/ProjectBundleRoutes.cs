using System.Reflection;
using Serilog;
using ShipRight.Modules.Projects;
using ShipRight.Modules.Resources.Stores;
using ShipRight.Modules.Servers;

namespace ShipRight.Modules.Transfer;

public static class ProjectBundleRoutes
{
    private const long MaxUploadBytes = 50L * 1024 * 1024;

    public static void MapProjectBundleRoutes(this WebApplication app)
    {
        app.MapPost("/api/projects/{id}/export", async (string id, TransferStores stores) =>
        {
            var bytes = await new ProjectBundleExporter(stores, CurrentVersion()).ExportAsync(id);
            if (bytes is null) return Results.NotFound(ProjectRouter.Error($"Project '{id}' not found."));
            Log.Information("Exported project {ProjectId} ({Bytes} bytes)", id, bytes.Length);
            return Results.File(bytes, "application/zip", $"{id}-shipright-export.zip");
        });

        // Raw zip bytes in the body: inspect only reports; import then writes.
        app.MapPost("/api/projects/import/inspect", (HttpRequest req, TransferStores stores) =>
            WithBundle(req, async bytes => Results.Ok(await new ProjectBundleInspector(stores, CurrentVersion()).InspectAsync(bytes))));

        app.MapPost("/api/projects/import", (HttpRequest req, TransferStores stores) =>
            WithBundle(req, async bytes =>
            {
                var outcome = await new ProjectBundleImporter(stores).ImportAsync(bytes);
                Log.Information("Imported project {ProjectId} ({ProjectName}) with {Warnings} warnings",
                    outcome.ProjectId, outcome.ProjectName, outcome.Warnings.Count);
                return Results.Ok(outcome);
            }));
    }

    private static async Task<IResult> WithBundle(HttpRequest req, Func<byte[], Task<IResult>> action)
    {
        if (req.ContentLength > MaxUploadBytes)
            return Results.BadRequest(ProjectRouter.Error("The file is too large to be a ShipRight export."));
        using var ms = new MemoryStream();
        await req.Body.CopyToAsync(ms);
        if (ms.Length > MaxUploadBytes)
            return Results.BadRequest(ProjectRouter.Error("The file is too large to be a ShipRight export."));
        try { return await action(ms.ToArray()); }
        catch (BundleException ex) { return Results.BadRequest(ProjectRouter.Error(ex.Message)); }
    }

    private static string CurrentVersion() =>
        (Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown")
            .Split('+')[0];

    public static void AddProjectBundle(this IServiceCollection services) =>
        services.AddSingleton(sp => new TransferStores(
            sp.GetRequiredService<IProjectStore>(), sp.GetRequiredService<IServerStore>(),
            sp.GetRequiredService<IDockerRegistryResourceStore>(), sp.GetRequiredService<IAwsProfileResourceStore>(),
            sp.GetRequiredService<ICredentialResourceStore>(), sp.GetRequiredService<IScriptResourceStore>(),
            sp.GetRequiredService<IPipelineResourceStore>()));
}
