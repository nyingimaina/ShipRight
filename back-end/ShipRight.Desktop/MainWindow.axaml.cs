using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Serilog;
using ShipRight.Desktop.Services;

namespace ShipRight.Desktop;

public partial class MainWindow : Window
{
    private readonly Services.ServerProcessManager _serverManager;
    private readonly Services.NotificationBridge? _notificationBridge;
    private readonly IWebView2Probe _probe;
    private bool _forceClose;

    public MainWindow(Services.ServerProcessManager serverManager) : this(serverManager, null, null) { }

    public MainWindow(Services.ServerProcessManager serverManager, Services.NotificationBridge notificationBridge)
        : this(serverManager, notificationBridge, null) { }

    public MainWindow(Services.ServerProcessManager serverManager, Services.NotificationBridge? notificationBridge, IWebView2Probe? probe)
    {
        _serverManager = serverManager;
        _notificationBridge = notificationBridge;
        InitializeComponent();
        _probe = probe ?? new WebView2Probe(Browser);

        if (_notificationBridge != null)
            _notificationBridge.Attach(Browser);

        Opened += async (_, _) =>
        {
            try
            {
                SetStatus("Starting server...", showProgress: true);
                await _serverManager.StartAsync();

                var drift = ServerProcessManager.CheckVersionDrift(
                    _serverManager.GetDesktopVersion(), _serverManager.ServerVersion);
                if (drift != ServerProcessManager.VersionDrift.None)
                {
                    Log.Warning("Version drift detected: {Drift} (Desktop v{Dv}, Server v{Sv})",
                        drift, _serverManager.GetDesktopVersion(), _serverManager.ServerVersion);
                }

                SetStatus("Connecting to dashboard...", showProgress: true);
                var dashboardUrl = new Uri("http://127.0.0.1:5200");
                var available = await _probe.ProbeAsync(TimeSpan.FromSeconds(5), dashboardUrl);
                SetStatus(available ? "Connected" : "Opening in browser...", showProgress: false);
                if (!available)
                    OpenBrowser();
            }
            catch (Exception ex)
            {
                SetStatus("Failed to connect", showProgress: false);
                Log.Error(ex, "Server startup failed");
            }
        };
    }

    private void SetStatus(string text, bool showProgress)
    {
        StatusText.Text = text;
        StatusProgress.IsVisible = showProgress;
    }

    public void NavigateToDashboard()
    {
        try
        {
            Browser.Source = new Uri("http://127.0.0.1:5200");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to navigate");
        }
    }

    public static void OpenBrowser()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("http://127.0.0.1:5200") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to open browser");
        }
    }

    public async void ForceClose()
    {
        _forceClose = true;
        Hide();
        await _serverManager.ShutdownAsync();
        Close();
    }

    public async void ShowAboutDialog()
    {
        var dv = _serverManager.GetDesktopVersion();
        var sv = _serverManager.ServerVersion ?? "unknown";
        var wv = _serverManager.WebVersion ?? "unknown";
        var wv2 = GetWebView2Version();
        var port = Services.ServerProcessManager.Port;

        var dialog = new Window
        {
            Title = "About ShipRight Desktop",
            Width = 420, Height = 300,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            WindowDecorations = WindowDecorations.Full
        };

        var root = new Border
        {
            Padding = new Thickness(24),
            Child = new StackPanel { Spacing = 6 }
        };
        var stack = (StackPanel)root.Child;

        stack.Children.Add(new TextBlock
        {
            Text = "ShipRight Desktop",
            FontSize = 20,
            FontWeight = Avalonia.Media.FontWeight.Bold,
            Margin = new Thickness(0, 0, 0, 8)
        });

        void AddEntry(string label, string value)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(0, 2) };
            var lbl = new TextBlock { Text = label, FontWeight = Avalonia.Media.FontWeight.SemiBold, MinWidth = 110, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            lbl.SetValue(Grid.ColumnProperty, 0);
            var val = new TextBlock { Text = value, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            val.SetValue(Grid.ColumnProperty, 1);
            grid.Children.AddRange([lbl, val]);
            stack.Children.Add(grid);
        }

        AddEntry("Desktop", $"v{dv}");
        AddEntry("Server", $"v{sv}");
        AddEntry("Web Front-end", $"v{wv}");
        AddEntry("Port", port.ToString());
        if (wv2 != null)
            AddEntry("WebView2 Runtime", wv2);

        stack.Children.Add(new Border
        {
            Height = 1,
            Background = Avalonia.Media.Brushes.LightGray,
            Margin = new Thickness(0, 8, 0, 8)
        });

        stack.Children.Add(new TextBlock
        {
            Text = "\u00A9 ShipRight",
            FontSize = 12,
            Foreground = Avalonia.Media.Brushes.Gray
        });

        var closeBtn = new Button
        {
            Content = "Close",
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Width = 80,
            Margin = new Thickness(0, 8, 0, 0)
        };
        closeBtn.Click += (_, _) => dialog.Close();
        stack.Children.Add(closeBtn);

        dialog.Content = root;
        await dialog.ShowDialog(this);
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
            Log.Warning("Failed to read WebView2 version from registry");
            return null;
        }
    }

    private void OnAboutClick(object? sender, RoutedEventArgs e) => ShowAboutDialog();

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (!_forceClose)
        {
            _forceClose = true;
            await _serverManager.ShutdownAsync();
        }
    }

    private async void OnExitClick(object? sender, RoutedEventArgs e)
    {
        _forceClose = true;
        Hide();
        await _serverManager.ShutdownAsync();
        Close();
    }
}
