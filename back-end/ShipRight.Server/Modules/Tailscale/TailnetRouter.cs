using Serilog;
using ShipRight.Modules.ServerNetwork;

namespace ShipRight.Modules.Tailscale;

public static class TailnetRouter
{
    public static void MapTailnetRoutes(this WebApplication app)
    {
        app.MapGet("/api/tailnets", async (ITailnetStore store, ITailnetManager manager) =>
        {
            var items = new List<TailnetView>();
            foreach (var connection in await store.GetAllAsync())
            {
                TailnetRuntime? runtime = null;
                try { runtime = await manager.GetStatusAsync(connection.Id); }
                catch (Exception ex) { Log.Debug(ex, "Tailnet status unavailable for {TailnetId}", connection.Id); }
                items.Add(View(connection, runtime));
            }
            return Results.Ok(items);
        });

        app.MapPost("/api/tailnets", async (TailnetRequest request, ITailnetStore store) =>
        {
            var existing = string.IsNullOrWhiteSpace(request.Id) ? null : await store.GetByIdAsync(request.Id);
            var error = Validate(request, existing is not null);
            if (error is not null) return Results.BadRequest(new { isError = true, message = error });

            var connection = new TailnetConnection
            {
                Id = string.IsNullOrWhiteSpace(request.Id) ? Guid.NewGuid().ToString("N") : request.Id,
                Name = request.Name.Trim(),
                Tailnet = string.IsNullOrWhiteSpace(request.Tailnet) ? "-" : request.Tailnet.Trim(),
                // A blank secret means "keep the stored one", so renaming or retagging never needs the key again.
                AuthKey = Keep(request.AuthKey, existing?.AuthKey),
                OAuthClientId = Keep(request.OAuthClientId, existing?.OAuthClientId),
                OAuthClientSecret = Keep(request.OAuthClientSecret, existing?.OAuthClientSecret),
                Tags = string.IsNullOrWhiteSpace(request.Tags) ? "tag:shipright" : request.Tags.Trim(),
            };
            await store.SaveAsync(connection);
            return Results.Ok(View(connection, null));
        });

        app.MapPost("/api/tailnets/{id}/start", async (string id, ITailnetManager manager) =>
        {
            try
            {
                var runtime = await manager.EnsureRunningAsync(id);
                return Results.Ok(new { running = true, online = runtime.Online, socksPort = runtime.SocksPort });
            }
            catch (NetworkUnavailableException ex)
            {
                return Results.BadRequest(new { isError = true, message = ex.Message });
            }
        });

        app.MapPost("/api/tailnets/{id}/stop", async (string id, ITailnetManager manager) =>
        {
            await manager.StopAsync(id);
            return Results.Ok(new { running = false });
        });

        app.MapPost("/api/tailnets/{id}/test", async (string id, ITailnetManager manager) =>
        {
            try
            {
                var runtime = await manager.GetStatusAsync(id);
                return Results.Ok(new { online = runtime.Online, socksPort = runtime.SocksPort });
            }
            catch (NetworkUnavailableException ex)
            {
                return Results.BadRequest(new { isError = true, message = ex.Message });
            }
        });

        app.MapDelete("/api/tailnets/{id}", async (string id, ITailnetStore store, ITailnetManager manager) =>
        {
            await manager.StopAsync(id);
            await store.DeleteAsync(id);
            return Results.Ok(new { message = "Tailscale network removed." });
        });
    }

    private static string? Validate(TailnetRequest request, bool isExisting)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) return "A name is required.";
        var hasKey = !string.IsNullOrWhiteSpace(request.AuthKey);
        var hasOAuth = !string.IsNullOrWhiteSpace(request.OAuthClientId) &&
                       !string.IsNullOrWhiteSpace(request.OAuthClientSecret);
        if (!hasKey && !hasOAuth && !isExisting)
            return "Add a Tailscale auth key, or an OAuth client id and secret.";
        return null;
    }

    private static string Keep(string? incoming, string? stored) =>
        string.IsNullOrWhiteSpace(incoming) ? stored ?? string.Empty : incoming.Trim();

    private static TailnetView View(TailnetConnection connection, TailnetRuntime? runtime) =>
        new(
            connection.Id,
            connection.Name,
            connection.Tailnet,
            connection.Tags,
            !string.IsNullOrWhiteSpace(connection.AuthKey),
            !string.IsNullOrWhiteSpace(connection.OAuthClientId),
            runtime?.Online ?? false,
            runtime?.Online == true ? "online" : "offline",
            runtime?.SocksPort ?? 0,
            connection.ModifiedAt);

    public sealed record TailnetView(
        string Id,
        string Name,
        string Tailnet,
        string Tags,
        bool HasAuthKey,
        bool HasOAuthClient,
        bool Online,
        string Status,
        int SocksPort,
        DateTimeOffset ModifiedAt);

    public sealed record TailnetRequest(
        string? Id,
        string Name,
        string? Tailnet,
        string? AuthKey,
        string? OAuthClientId,
        string? OAuthClientSecret,
        string? Tags);
}
