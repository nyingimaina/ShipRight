namespace ShipRight.Modules.Notifications;

public sealed record TelegramTestRequest(string? Text);

public static class NotificationRoutes
{
    public static void MapNotificationRoutes(this WebApplication app)
    {
        // What is configured, without ever returning the token or chat id.
        app.MapGet("/api/notifications/status", (TelegramCliNotifier notifier) =>
        {
            var settings = notifier.Settings;
            return Results.Ok(new
            {
                telegram = new
                {
                    cli = settings.CliPath,
                    tokenConfigured = settings.TokenConfigured,
                    chatConfigured = settings.ChatConfigured,
                },
            });
        });

        // A deliberate test: it sends one message. A failure is a bad request with a kind and a plain reason.
        app.MapPost("/api/notifications/telegram/test", async (
            TelegramTestRequest? body, TelegramCliNotifier notifier, CancellationToken ct) =>
        {
            var text = string.IsNullOrWhiteSpace(body?.Text)
                ? "ShipRight: Telegram notifications are working."
                : body.Text.Trim();

            if (text.Length > 4000)
                return Results.BadRequest(new
                {
                    isError = true,
                    message = "Keep the test message under 4000 characters; ShipRight does not split a test.",
                });

            var result = await notifier.SendAsync(text, ct);
            return result.Ok
                ? Results.Ok(new { ok = true, parts = result.Parts })
                : Results.BadRequest(new
                {
                    isError = true,
                    kind = result.ErrorKind,
                    message = result.ErrorMessage,
                });
        });
    }
}
