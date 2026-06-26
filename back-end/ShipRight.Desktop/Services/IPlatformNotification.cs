namespace ShipRight.Desktop.Services;

public interface IPlatformNotification
{
    Task ShowAsync(string title, string message, string? notificationType = null);
}
