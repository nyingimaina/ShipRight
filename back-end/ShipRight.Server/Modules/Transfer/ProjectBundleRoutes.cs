using System.Reflection;
using Serilog;
using ShipRight.Modules.JitDeploy;
using ShipRight.Modules.Projects;
using ShipRight.Modules.RemoteHost;
using ShipRight.Modules.Resources.Stores;
using ShipRight.Modules.Servers;

namespace ShipRight.Modules.Transfer;

public static class ProjectBundleRoutes
{
    private const long MaxUploadBytes = 50L * 1024 * 1024;
    private const string PassphraseHeader = "X-ShipRight-Passphrase";
    private const string TargetRootHeader = "X-ShipRight-Target-Root";
    private const string AcknowledgedHeader = "X-ShipRight-Secrets-Acknowledged";

    public static void MapProjectBundleRoutes(this WebApplication app)
    {
        app.MapPost("/api/projects/{id}/export", async (string id, HttpRequest req, TransferStores stores) =>
        {
            var includeSecrets = bool.TryParse(req.Query["includeSecrets"], out var requested) && requested;
            var passphrase = Header(req, PassphraseHeader);
            if (includeSecrets)
            {
                if (!string.Equals(Header(req, AcknowledgedHeader), "true", StringComparison.OrdinalIgnoreCase))
                    return Results.BadRequest(ProjectRouter.Error(
                        "Confirm that you understand the risk before exporting secrets, keys and credentials."));
                if (string.IsNullOrEmpty(passphrase))
                    return Results.BadRequest(ProjectRouter.Error("Enter a passphrase to protect this export."));
            }

            var bytes = await new ProjectBundleExporter(stores, CurrentVersion(), includeSecrets).ExportAsync(id);
            if (bytes is null) return Results.NotFound(ProjectRouter.Error($"Project '{id}' not found."));
            if (includeSecrets) bytes = BundleCrypto.Encrypt(bytes, passphrase!);

            Log.Information("Exported project {ProjectId} ({Bytes} bytes, secrets: {Secrets})", id, bytes.Length, includeSecrets);
            var fileName = includeSecrets ? $"{id}-shipright-secret-export.shrsec" : $"{id}-shipright-export.zip";
            var contentType = includeSecrets ? "application/octet-stream" : "application/zip";
            return Results.File(bytes, contentType, fileName);
        });

        // Raw zip (or encrypted envelope) bytes in the body: inspect only reports; import then writes.
        app.MapPost("/api/projects/import/inspect", (HttpRequest req, TransferStores stores) =>
            WithBundle(req, async bytes => Results.Ok(await new ProjectBundleInspector(
                stores, CurrentVersion(), Header(req, PassphraseHeader)).InspectAsync(bytes))));

        app.MapPost("/api/projects/import", (HttpRequest req, TransferStores stores) =>
            WithBundle(req, async bytes =>
            {
                var outcome = await new ProjectBundleImporter(
                    stores, Header(req, TargetRootHeader), Header(req, PassphraseHeader)).ImportAsync(bytes);
                Log.Information("Imported project {ProjectId} ({ProjectName}) with {Warnings} warnings",
                    outcome.ProjectId, outcome.ProjectName, outcome.Warnings.Count);
                return Results.Ok(outcome);
            }));
    }

    private static string? Header(HttpRequest req, string name)
    {
        var value = req.Headers[name].ToString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
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
            sp.GetRequiredService<IPipelineResourceStore>(), sp.GetRequiredService<IKeyFileResourceStore>(),
            sp.GetRequiredService<SshKeyStore>(), sp.GetRequiredService<IJitConfigStore>()));
}
