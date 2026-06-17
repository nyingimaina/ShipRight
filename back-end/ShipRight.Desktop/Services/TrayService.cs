using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace ShipRight.Desktop.Services;

public class TrayService
{
    private readonly ServerProcessManager _serverManager;
    private readonly MainWindow _mainWindow;
    private TrayIcon? _trayIcon;

    public TrayService(ServerProcessManager serverManager, MainWindow mainWindow)
    {
        _serverManager = serverManager;
        _mainWindow = mainWindow;
    }

    public void Setup()
    {
        var desktopVersion = _serverManager.GetDesktopVersion();

        _trayIcon = new TrayIcon
        {
            ToolTipText = $"ShipRight Desktop v{desktopVersion}",
            Icon = new WindowIcon(LoadAppIcon())
        };

        var openItem = new NativeMenuItem("Open Dashboard");
        openItem.Click += (_, _) =>
        {
            _mainWindow.Show();
            _mainWindow.NavigateToDashboard();
            _mainWindow.Activate();
        };

        var aboutItem = new NativeMenuItem("About ShipRight Desktop");
        aboutItem.Click += (_, _) => _mainWindow.ShowAboutDialog();

        var exitItem = new NativeMenuItem("Exit");
        exitItem.Click += (_, _) => _mainWindow.ForceClose();

        var menu = new NativeMenu();
        menu.Add(openItem);
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(aboutItem);
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(exitItem);

        _trayIcon.Menu = menu;
        _trayIcon.IsVisible = true;
    }

    private async void ShowAboutDialog()
    {
        var desktopVersion = _serverManager.GetDesktopVersion();
        var serverVersion = _serverManager.ServerVersion ?? "unknown";
        var webView2Version = GetWebView2Version();

        var desktopApp = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
        var mainWindow = desktopApp?.MainWindow;

        if (mainWindow == null) return;

        var dialog = new Window
        {
            Title = "About ShipRight Desktop",
            Width = 400,
            Height = 250,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            WindowDecorations = WindowDecorations.Full
        };

        var stack = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 10
        };

        stack.Children.Add(new TextBlock
        {
            Text = "ShipRight Desktop",
            FontSize = 18,
            FontWeight = Avalonia.Media.FontWeight.Bold
        });

        stack.Children.Add(new TextBlock
        {
            Text = $"Desktop: v{desktopVersion}"
        });

        stack.Children.Add(new TextBlock
        {
            Text = $"Server:  v{serverVersion}"
        });

        if (webView2Version != null)
        {
            stack.Children.Add(new TextBlock
            {
                Text = $"WebView2 Runtime: {webView2Version}",
                FontSize = 12,
                Foreground = Avalonia.Media.Brushes.Gray
            });
        }

        stack.Children.Add(new TextBlock
        {
            Text = "\u00A9 ShipRight",
            FontSize = 12,
            Foreground = Avalonia.Media.Brushes.Gray,
            Margin = new Thickness(0, 10, 0, 0)
        });

        var closeButton = new Button
        {
            Content = "Close",
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Width = 80
        };
        closeButton.Click += (_, _) => dialog.Close();
        stack.Children.Add(closeButton);

        dialog.Content = stack;
        await dialog.ShowDialog(mainWindow);
    }

    private static string? GetWebView2Version()
    {
        try
        {
            var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}");
            return key?.GetValue("pv")?.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static Avalonia.Media.Imaging.Bitmap LoadAppIcon()
    {
        var icoPath = Path.Combine(AppContext.BaseDirectory, "shipright-desktop.ico");
        if (File.Exists(icoPath))
        {
            using var stream = File.OpenRead(icoPath);
            return new Avalonia.Media.Imaging.Bitmap(stream);
        }

        var serverIcoPath = Path.Combine(AppContext.BaseDirectory, "shipright.ico");
        if (File.Exists(serverIcoPath))
        {
            using var stream = File.OpenRead(serverIcoPath);
            return new Avalonia.Media.Imaging.Bitmap(stream);
        }

        var writeableBitmap = new Avalonia.Media.Imaging.WriteableBitmap(
            new Avalonia.PixelSize(32, 32),
            new Avalonia.Vector(96, 96),
            Avalonia.Platform.PixelFormat.Bgra8888);
        return writeableBitmap;
    }
}
