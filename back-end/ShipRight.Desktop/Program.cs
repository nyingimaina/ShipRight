using System.Reflection;
using System.Threading;
using Avalonia;
using Serilog;

namespace ShipRight.Desktop;

internal static class Program
{
    private const string MutexName = "ShipRight.Desktop";
    private static Mutex? _mutex;

    [STAThread]
    private static void Main(string[] args)
    {
        _mutex = new Mutex(true, MutexName, out var isNew);
        if (!isNew)
        {
            return;
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        // WebView2 needs a writable user data folder — can't use Program Files
        Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER",
            Path.Combine(appData, "ShipRight", "WebView2"));

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console()
            .WriteTo.File(
                Path.Combine(appData, "ShipRight", "logs", "desktop-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7)
            .CreateLogger();

        try
        {
            var serverManager = new Services.ServerProcessManager();
            BuildAvaloniaApp(serverManager).StartWithClassicDesktopLifetime(args);
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

    private static AppBuilder BuildAvaloniaApp(Services.ServerProcessManager serverManager)
    {
        return AppBuilder.Configure(() => new App(serverManager))
            .UsePlatformDetect()
            .LogToTrace();
    }
}
