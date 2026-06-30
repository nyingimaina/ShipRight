using Microsoft.Toolkit.Uwp.Notifications;
using Serilog;

namespace ShipRight.Desktop.Services;

public class WindowsToastNotification : IPlatformNotification
{
    public Task ShowAsync(string title, string message, string? notificationType = null)
    {
        try
        {
            var builder = new ToastContentBuilder()
                .AddText(title)
                .AddText(message);

            if (notificationType?.EndsWith("_failed") == true)
                builder.AddAudio(new Uri("ms-winsoundevent:Notification.IM"));
            else if (notificationType == "pause_required")
                builder.AddAudio(new Uri("ms-winsoundevent:Notification.Looping.Alarm"), loop: true);
            else
                builder.AddAudio(new Uri("ms-winsoundevent:Notification.Default"));

            builder.Show();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to show Windows toast notification");
        }

        return Task.CompletedTask;
    }
}
