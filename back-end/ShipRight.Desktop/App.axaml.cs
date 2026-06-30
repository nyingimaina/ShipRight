using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace ShipRight.Desktop;

public partial class App : Application
{
    private readonly Services.ServerProcessManager _serverManager;
    private readonly Services.NotificationBridge _notificationBridge;

    public App(Services.ServerProcessManager serverManager, Services.NotificationBridge notificationBridge)
    {
        _serverManager = serverManager;
        _notificationBridge = notificationBridge;
    }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = new MainWindow(_serverManager, _notificationBridge);
            desktop.MainWindow = mainWindow;

            desktop.ShutdownRequested += async (_, _) =>
            {
                await _serverManager.ShutdownAsync();
            };

            desktop.MainWindow.Show();

            var trayService = new Services.TrayService(_serverManager, mainWindow);
            trayService.Setup();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
