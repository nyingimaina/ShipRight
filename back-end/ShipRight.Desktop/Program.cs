using System.Reflection;
using System.Threading;
using Avalonia;
using Serilog;
using ShipRight.RuntimeConfig;

namespace ShipRight.Desktop;

internal static class Program
{
    private static Mutex? _mutex;

    [STAThread]
    private static void Main(string[] args)
    {
        var profile = AppProfile.Resolve(
            args,
            Environment.GetEnvironmentVariable("SHIPRIGHT_PROFILE"),
            Environment.GetEnvironmentVariable("SHIPRIGHT_PORT"),
            Environment.GetEnvironmentVariable("SHIPRIGHT_DATA_DIR"),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

        _mutex = new Mutex(true, profile.MutexName, out var isNew);
        if (!isNew)
        {
            return;
        }

        Directory.CreateDirectory(profile.WebView2Directory);
        Directory.CreateDirectory(profile.DesktopLogDirectory);

        Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", profile.WebView2Directory);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console()
            .WriteTo.File(
                Path.Combine(profile.DesktopLogDirectory, "desktop-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7)
            .CreateLogger();

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Fatal((Exception?)e.ExceptionObject, "Unhandled AppDomain exception");

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Fatal(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };

        try
        {
            var serverManager = new Services.ServerProcessManager(profile);
            var notification = new Services.WindowsToastNotification();
            var notificationBridge = new Services.NotificationBridge(notification);
            BuildAvaloniaApp(serverManager, notificationBridge).StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "ShipRight Desktop failed to start");
        }
        finally
        {
            Log.CloseAndFlush();
            _mutex.ReleaseMutex();
            _mutex.Dispose();
        }
    }

    private static AppBuilder BuildAvaloniaApp(Services.ServerProcessManager serverManager, Services.NotificationBridge notificationBridge)
    {
        return AppBuilder.Configure(() => new App(serverManager, notificationBridge))
            .UsePlatformDetect()
            .LogToTrace();
    }
}
