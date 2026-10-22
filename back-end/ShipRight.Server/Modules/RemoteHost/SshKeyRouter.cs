using Serilog;
using ShipRight.Modules.Projects;
using ShipRight.Modules.Resources.Stores;
using ShipRight.Modules.Servers;

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
                    await remoteHost.AuthorizeKeyAsync(config, req.Password ?? string.Empty, publicKey);
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

    // ── Server-scoped SSH key endpoints (mirrors project endpoints, keyed by "server-{serverId}") ──

    public static void MapServerSshKeyRoutes(this WebApplication app)
    {
        app.MapGet("/api/servers/{id}/ssh-key/status",
            async (string id, IServerStore store, SshKeyStore keyStore) =>
            {
                var server = await store.GetByIdAsync(id);
                if (server is null)
                    return Results.NotFound(new { isError = true, message = $"Server '{id}' not found." });

                var keyId = $"server-{id}";
                var exists = await keyStore.ExistsAsync(keyId);
                string? publicKey = exists ? await keyStore.GetPublicKeyAsync(keyId) : null;
                return Results.Ok(new
                {
                    exists,
                    publicKey,
                    managedSshKey = server.ManagedSshKey,
                    authorized = server.ManagedSshKeyAuthorized,
                });
            });

        app.MapPost("/api/servers/{id}/ssh-key/generate",
            async (string id, IServerStore store, SshKeyStore keyStore) =>
            {
                var server = await store.GetByIdAsync(id);
                if (server is null)
                    return Results.NotFound(new { isError = true, message = $"Server '{id}' not found." });

                var keyId = $"server-{id}";
                try
                {
                    await keyStore.GenerateAsync(keyId);
                }
                catch (Exception ex)
                {
                    // A failed generation leaves no key behind and must not mark the server as managed (R1).
                    Log.Warning(ex, "Managed SSH key generation failed for server {ServerId}", id);
                    return Results.BadRequest(new
                    {
                        isError = true,
                        message = "Key generation is not available on this machine. Ensure ssh-keygen is installed, then try again."
                    });
                }

                var publicKey = await keyStore.GetPublicKeyAsync(keyId);
                var keyPath = keyStore.GetPrivateKeyPath(keyId);

                // Regenerating replaces the managed key, so any earlier authorization no longer applies (R1).
                var updated = server with
                {
                    SshKeyPath = keyPath,
                    ManagedSshKey = true,
                    ManagedSshKeyAuthorized = false,
                };
                await store.SaveAsync(updated);

                Log.Information("Managed SSH key generated for server {ServerId}", id);
                return Results.Ok(new { publicKey, keyPath, authorized = false });
            });

        app.MapPost("/api/servers/{id}/ssh-key/authorize",
            async (string id, AuthorizeKeyRequest req, IServerStore store,
                   SshKeyStore keyStore, IKeyFileResourceStore keyFiles, IRemoteHostProvider remoteHost) =>
            {
                var server = await store.GetByIdAsync(id);
                if (server is null)
                    return Results.NotFound(new { isError = true, message = $"Server '{id}' not found." });

                var keyId = $"server-{id}";
                if (!await keyStore.ExistsAsync(keyId))
                    return Results.BadRequest(new
                    {
                        isError = true,
                        message = "Generate an SSH key first before authorizing."
                    });

                var publicKey = await keyStore.GetPublicKeyAsync(keyId);
                var config = new RemoteHostConfig(server.Host, req.Port ?? 22, server.Username);
                var useKeyFile = string.Equals(req.Mode, "keyfile", StringComparison.OrdinalIgnoreCase);

                try
                {
                    if (useKeyFile)
                    {
                        if (req.KeyFileResourceId is not { } fileId || fileId == Guid.Empty)
                            return Results.ValidationProblem(new Dictionary<string, string[]>
                            {
                                ["keyFileResourceId"] = ["Choose a saved key file."],
                            });

                        var keyFile = await keyFiles.GetByIdAsync(fileId);
                        if (keyFile is null)
                            return Results.NotFound(new { isError = true, message = "That key file was not found." });

                        await remoteHost.AuthorizeKeyWithFileAsync(config, keyFile.Content, publicKey);
                    }
                    else
                    {
                        if (string.IsNullOrEmpty(req.Password))
                            return Results.ValidationProblem(new Dictionary<string, string[]>
                            {
                                ["password"] = ["Enter the server password, or choose a saved key file."],
                            });

                        await remoteHost.AuthorizeKeyAsync(config, req.Password, publicKey);
                    }
                }
                catch (Exception ex)
                {
                    // A failed authorization must not mark the key as authorized or touch the server settings (R2).
                    Log.Warning(ex, "SSH key authorization failed for server {ServerId}", id);
                    return Results.BadRequest(new { isError = true, message = $"Authorization failed: {ex.Message}" });
                }

                await store.SaveAsync(server with { ManagedSshKey = true, ManagedSshKeyAuthorized = true });
                Log.Information("SSH key authorized on server {ServerId}", id);
                return Results.Ok(new { message = "SSH key authorized. ShipRight will use it for all connections to this server." });
            });
    }

    private record AuthorizeKeyRequest(string? Password, int? Port, string? Mode, Guid? KeyFileResourceId);
}
