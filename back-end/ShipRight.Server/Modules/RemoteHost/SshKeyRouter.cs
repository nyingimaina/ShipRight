using Serilog;
using ShipRight.Modules.Projects;

namespace ShipRight.Modules.RemoteHost;

public static class SshKeyRouter
{
    public static void MapSshKeyRoutes(this WebApplication app)
    {
        app.MapGet("/api/projects/{id}/ssh-key/status",
            async (string id, IProjectStore store, SshKeyStore keyStore) =>
            {
                var project = await store.GetByIdAsync(id);
                if (project is null)
                    return Results.NotFound(new { isError = true, message = $"Project '{id}' not found." });

                var exists = await keyStore.ExistsAsync(id);
                string? publicKey = exists ? await keyStore.GetPublicKeyAsync(id) : null;
                return Results.Ok(new { exists, publicKey, managedSshKey = project.Server.ManagedSshKey });
            });

        app.MapPost("/api/projects/{id}/ssh-key/generate",
            async (string id, IProjectStore store, SshKeyStore keyStore) =>
            {
                var project = await store.GetByIdAsync(id);
                if (project is null)
                    return Results.NotFound(new { isError = true, message = $"Project '{id}' not found." });

                await keyStore.GenerateAsync(id);
                var publicKey = await keyStore.GetPublicKeyAsync(id);
                var keyPath = keyStore.GetPrivateKeyPath(id);

                var updated = project with
                {
                    Server = project.Server with
                    {
                        SshKeyPath = keyPath,
                        ManagedSshKey = true,
                    },
                    ModifiedAt = DateTime.UtcNow,
                };
                await store.SaveAsync(updated);

                Log.Information("Managed SSH key generated for project {ProjectId}", id);
                return Results.Ok(new { publicKey, keyPath });
            });

        app.MapPost("/api/projects/{id}/ssh-key/authorize",
            async (string id, AuthorizeKeyRequest req, IProjectStore store,
                   SshKeyStore keyStore, IRemoteHostProvider remoteHost) =>
            {
                var project = await store.GetByIdAsync(id);
                if (project is null)
                    return Results.NotFound(new { isError = true, message = $"Project '{id}' not found." });

                if (!await keyStore.ExistsAsync(id))
                    return Results.BadRequest(new
                    {
                        isError = true,
                        message = "Generate an SSH key first before authorizing."
                    });

                var publicKey = await keyStore.GetPublicKeyAsync(id);
                var config = new RemoteHostConfig(
                    project.Server.Host,
                    req.Port ?? 22,
                    project.Server.Username);

                try
                {
                    await remoteHost.AuthorizeKeyAsync(config, req.Password, publicKey);
                    Log.Information("SSH key authorized on server for project {ProjectId}", id);
                    return Results.Ok(new { message = "SSH key authorized. ShipRight will use it for all deployments." });
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "SSH key authorization failed for project {ProjectId}", id);
                    return Results.BadRequest(new { isError = true, message = $"Authorization failed: {ex.Message}" });
                }
            });
    }

    private record AuthorizeKeyRequest(string Password, int? Port);
}
