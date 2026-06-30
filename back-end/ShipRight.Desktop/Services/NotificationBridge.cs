using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Controls;
using Serilog;

namespace ShipRight.Desktop.Services;

public class NotificationBridge
{
    private readonly IPlatformNotification _notification;
    private bool _attached;

    public NotificationBridge(IPlatformNotification notification)
    {
        _notification = notification;
    }

    public void Attach(NativeWebView webView)
    {
        if (_attached) return;
        _attached = true;

        webView.WebMessageReceived += OnWebMessageReceived;
    }

    public void Detach(NativeWebView webView)
    {
        if (!_attached) return;
        _attached = false;

        webView.WebMessageReceived -= OnWebMessageReceived;
    }

    private void OnWebMessageReceived(object? sender, WebMessageReceivedEventArgs e)
    {
        ProcessMessage(e.Body ?? "");
    }

    public void ProcessMessage(string body)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(body)) return;
            var msg = JsonSerializer.Deserialize<NotificationMessage>(body);
            if (msg?.Type != "notification" || string.IsNullOrWhiteSpace(msg.Title))
                return;

            _ = _notification.ShowAsync(msg.Title, msg.Message ?? "", msg.NotificationType);
        }
        catch (JsonException ex)
        {
            Log.Warning(ex, "Failed to parse notification bridge message");
        }
    }

    private sealed class NotificationMessage
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("notificationType")]
        public string? NotificationType { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }

        [JsonPropertyName("tag")]
        public string? Tag { get; set; }
    }
}
