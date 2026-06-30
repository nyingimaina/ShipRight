# ShipRight Desktop Enhancement Guide

This guide shows how to bring the ShipRight Desktop app to parity with SnapTask's Avalonia wrapper, startup resilience, and UI/UX improvements. Each enhancement includes the **SnapTask approach**, **step-by-step instructions**, and **TDD test examples**.

---

## Contents

1. [WebView2 Probe (graceful fallback to browser)](#1-webview2-probe)
2. [Catch-Block Logging + Global Error Handlers](#2-catch-block-logging)
3. [Exponential Backoff for Server Startup](#3-exponential-backoff)
4. [Port Conflict Detection](#4-port-conflict-detection)
5. [Version Drift Prevention](#5-version-drift-prevention)
6. [Status Bar with Progress Indicator](#6-status-bar)

---

## 1. WebView2 Probe

**Problem**: The Desktop's `NativeWebView` immediately navigates to the dashboard URL on `Opened`. If WebView2 is not installed or fails to initialize, the WebView stays blank with no fallback.

**Solution**: Probe WebView2 before navigating — listen for `NavigationCompleted` with a timeout. If the event fires, WebView2 works. If it times out, open the OS browser instead.

### Step 1: Create `IWebViewNavigationController` interface

**File**: `Services/IWebViewNavigationController.cs`
```csharp
using Avalonia.Controls;

namespace ShipRight.Desktop.Services;

public interface IWebViewNavigationController
{
    event EventHandler<WebViewNavigationCompletedEventArgs>? NavigationCompleted;
    Uri? Source { set; }
}
```

### Step 2: Create `WebViewNavigationAdapter` (wraps real `NativeWebView`)

**File**: `Services/WebViewNavigationAdapter.cs`
```csharp
using Avalonia.Controls;

namespace ShipRight.Desktop.Services;

public class WebViewNavigationAdapter : IWebViewNavigationController
{
    private readonly NativeWebView _webView;

    public WebViewNavigationAdapter(NativeWebView webView)
    {
        _webView = webView;
    }

    public event EventHandler<WebViewNavigationCompletedEventArgs>? NavigationCompleted
    {
        add => _webView.NavigationCompleted += value;
        remove => _webView.NavigationCompleted -= value;
    }

    public Uri? Source
    {
        set => _webView.Source = value;
    }
}
```

### Step 3: Create `IWebView2Probe` interface

**File**: `Services/IWebView2Probe.cs`
```csharp
namespace ShipRight.Desktop.Services;

public interface IWebView2Probe
{
    Task<bool> ProbeAsync(TimeSpan timeout, Uri targetUrl);
}
```

### Step 4: Create `WebView2Probe` implementation

**File**: `Services/WebView2Probe.cs`
```csharp
using Avalonia.Controls;

namespace ShipRight.Desktop.Services;

public class WebView2Probe : IWebView2Probe
{
    private readonly IWebViewNavigationController _webView;

    public WebView2Probe(NativeWebView webView) : this(new WebViewNavigationAdapter(webView)) { }

    public WebView2Probe(IWebViewNavigationController webView)
    {
        _webView = webView;
    }

    public async Task<bool> ProbeAsync(TimeSpan timeout, Uri targetUrl)
    {
        var tcs = new TaskCompletionSource<bool>();

        EventHandler<WebViewNavigationCompletedEventArgs> handler = (_, args) =>
            tcs.TrySetResult(args.IsSuccess);

        _webView.NavigationCompleted += handler;
        _webView.Source = targetUrl;

        try
        {
            return await tcs.Task.WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            return false;
        }
        finally
        {
            _webView.NavigationCompleted -= handler;
        }
    }
}
```

### Step 5: Update `MainWindow` to probe before navigating

```csharp
public partial class MainWindow : Window
{
    private readonly ServerProcessManager _serverManager;
    private readonly IWebView2Probe _probe;
    private bool _forceClose;

    public MainWindow(ServerProcessManager serverManager, IWebView2Probe? probe = null)
    {
        _serverManager = serverManager;
        InitializeComponent();
        _probe = probe ?? new WebView2Probe(Browser);
        Opened += async (_, _) =>
        {
            try
            {
                await _serverManager.StartAsync();
                var dashboardUrl = new Uri("http://127.0.0.1:5200");
                var available = await _probe.ProbeAsync(TimeSpan.FromSeconds(5), dashboardUrl);
                if (!available)
                    OpenBrowser();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Server startup failed");
            }
        };
    }

    public static void OpenBrowser()
    {
        try
        {
            Process.Start(new ProcessStartInfo("http://127.0.0.1:5200") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to open browser");
        }
    }
}
```

### TDD Test Example

**Test file**: `DesktopNavigationTests.cs`
```csharp
using Avalonia.Controls;
using ShipRight.Desktop.Services;

namespace ShipRight.Tests;

public class DesktopNavigationTests
{
    private static readonly Uri TestUrl = new("http://example.com");

    [Fact]
    public async Task WebView2Probe_ReturnsTrue_WhenNavigationCompletesBeforeTimeout()
    {
        var webView = new FakeWebView();
        var probe = new WebView2Probe(webView);

        var probeTask = probe.ProbeAsync(TimeSpan.FromSeconds(5), TestUrl);
        webView.FireNavigationCompleted(true);

        Assert.True(await probeTask);
    }

    [Fact]
    public async Task WebView2Probe_ReturnsFalse_WhenNavigationTimesOut()
    {
        var webView = new FakeWebView();
        var probe = new WebView2Probe(webView);

        var result = await probe.ProbeAsync(TimeSpan.FromMilliseconds(50), TestUrl);
        Assert.False(result);
    }

    [Fact]
    public async Task WebView2Probe_ReturnsFalse_WhenNavigationFails()
    {
        var webView = new FakeWebView();
        var probe = new WebView2Probe(webView);

        var probeTask = probe.ProbeAsync(TimeSpan.FromSeconds(5), TestUrl);
        webView.FireNavigationCompleted(false);

        Assert.False(await probeTask);
    }
}

public class FakeWebView : IWebViewNavigationController
{
    public event EventHandler<WebViewNavigationCompletedEventArgs>? NavigationCompleted;
    public Uri? Source { get; set; }

    public void FireNavigationCompleted(bool success)
    {
        NavigationCompleted?.Invoke(this, new WebViewNavigationCompletedEventArgs
        {
            IsSuccess = success,
            Request = Source
        });
    }
}
```

### Test project setup

Add to test `.csproj`:
```xml
<ItemGroup>
  <PackageReference Include="Avalonia.Controls.WebView" Version="12.0.1" />
</ItemGroup>
```

Add `InternalsVisibleTo` to **Desktop** `.csproj`:
```xml
<ItemGroup>
  <InternalsVisibleTo Include="ShipRight.Tests" />
</ItemGroup>
```

---

## 2. Catch-Block Logging

**Problem**: 14 out of 26 catch blocks in SnapTask were bare (`catch { }`) — silent failures with no trace in logs.

**Solution**: Every catch block must log using Serilog. Add global error handlers for unhandled exceptions.

### Audit your catch blocks

Search every `.cs` file for `catch`:
```bash
rg -n "catch" --include "*.cs" back-end/
```

For each bare catch, add:
- `Log.Warning(ex, "descriptive message")` for recoverable failures
- `Log.Error(ex, "descriptive message")` for unexpected failures
- Always log the exception as the first argument (Serilog captures stack trace)

### Desktop Program.cs — Add global handlers

Add **before** the `try` block in `Main()`:
```csharp
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    Log.Fatal((Exception?)e.ExceptionObject, "Unhandled AppDomain exception");

TaskScheduler.UnobservedTaskException += (_, e) =>
{
    Log.Fatal(e.Exception, "Unobserved task exception");
    e.SetObserved();
};
```

### Example of bad → good

**Bad**:
```csharp
catch { return null; }
```

**Good**:
```csharp
catch
{
    Log.Warning("Failed to read version from registry");
    return null;
}
```

### Controllers that return `ex.Message` to the client

Also log server-side:
```csharp
catch (Exception ex)
{
    Log.Error(ex, "Export failed for {Path}", req.Path);
    return BadRequest($"Export failed: {ex.Message}");
}
```

### Exceptions you should NOT log

- `TimeoutException` in `WebView2Probe` — it's the probe mechanism, expected
- `OperationCanceledException` in Slack/WebSocket loops — expected cancellation

### API global exception middleware

Already exists in ShipRight Server `Program.cs` (lines 70-81), but ensure it logs the exception *object*, not just a message:

```csharp
app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    var error = context.Features.Get<IExceptionHandlerFeature>();
    if (error != null)
        Log.Fatal(error.Error, "Unhandled exception on {Path}", context.Request.Path);
    context.Response.StatusCode = 500;
    await context.Response.WriteAsJsonAsync(new { isError = true, message = "Internal error" });
}));
```

---

## 3. Exponential Backoff

**Problem**: `PollHealthAsync` uses fixed 500ms intervals (20 retries = 10s). If the server starts quickly, you still wait 500ms. If the server is slow, you waste time on too-frequent retries.

**Solution**: Start with short delays and grow exponentially, capped at a maximum, with jitter.

### Before

```csharp
private async Task<bool> PollHealthAsync(TimeSpan interval, int maxRetries)
{
    for (var i = 0; i < maxRetries; i++)
    {
        if (await HealthCheckAsync())
            return true;
        await Task.Delay(interval);
    }
    return false;
}

// Called as:
if (!await PollHealthAsync(TimeSpan.FromMilliseconds(500), 20))
```

### After

```csharp
private async Task<bool> PollHealthAsync(int maxRetries = 10)
{
    return await PollWithExponentialBackoffAsync(HealthCheckAsync, maxRetries);
}

internal static TimeSpan ComputeBackoffDelay(int attemptIndex, TimeSpan baseDelay, TimeSpan maxDelay)
{
    var exponentialMs = baseDelay.TotalMilliseconds * Math.Pow(2, attemptIndex);
    var clamped = Math.Min(exponentialMs, maxDelay.TotalMilliseconds);
    return TimeSpan.FromMilliseconds(clamped);
}

internal static async Task<bool> PollWithExponentialBackoffAsync(
    Func<Task<bool>> check, int maxRetries = 10)
{
    var baseDelay = TimeSpan.FromMilliseconds(50);
    var maxDelay = TimeSpan.FromMilliseconds(2000);
    var rng = new Random();

    for (var i = 0; i < maxRetries; i++)
    {
        if (await check())
            return true;

        var delay = ComputeBackoffDelay(i, baseDelay, maxDelay);
        var jitter = rng.Next(
            (int)(-delay.TotalMilliseconds * 0.25),
            (int)(delay.TotalMilliseconds * 0.25) + 1);
        await Task.Delay(delay + TimeSpan.FromMilliseconds(jitter));
    }

    return false;
}
```

### Delay sequence (no jitter)

| Attempt | Delay | Cumulative |
|---------|-------|------------|
| 0 | 50ms | 50ms |
| 1 | 100ms | 150ms |
| 2 | 200ms | 350ms |
| 3 | 400ms | 750ms |
| 4 | 800ms | 1.55s |
| 5 | 1600ms | 3.15s |
| 6-9 | 2000ms (capped) | ~11.15s |

### TDD Test Example

```csharp
public class ExponentialBackoffTests
{
    [Fact]
    public async Task Backoff_ReturnsTrue_WhenFirstAttemptSucceeds()
    {
        var result = await ServerProcessManager.PollWithExponentialBackoffAsync(
            () => Task.FromResult(true), maxRetries: 5);
        Assert.True(result);
    }

    [Fact]
    public async Task Backoff_ReturnsFalse_WhenAllAttemptsFail()
    {
        var result = await ServerProcessManager.PollWithExponentialBackoffAsync(
            () => Task.FromResult(false), maxRetries: 3);
        Assert.False(result);
    }

    [Fact]
    public void Backoff_DelaysIncrease()
    {
        var baseDelay = TimeSpan.FromMilliseconds(50);
        var maxDelay = TimeSpan.FromMilliseconds(2000);

        var delays = Enumerable.Range(0, 8)
            .Select(i => ServerProcessManager.ComputeBackoffDelay(i, baseDelay, maxDelay))
            .ToList();

        for (var i = 1; i < delays.Count; i++)
            Assert.True(delays[i] >= delays[i - 1]);
    }

    [Fact]
    public void Backoff_DelaysCapAtMaxDelay()
    {
        var delay = ServerProcessManager.ComputeBackoffDelay(6,
            TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(2000));
        Assert.Equal(TimeSpan.FromMilliseconds(2000), delay);
    }
}
```

---

## 4. Port Conflict Detection

**Problem**: Before starting the server, we health-check the port. If the health check fails (connection refused), we start the server. But if another process is listening on the port, the server will fail to start with a cryptic error.

**Solution**: Before starting, check the system's active TCP listeners for our port.

### Implementation

Add to `ServerProcessManager.cs`:

```csharp
using System.Net;
using System.Net.NetworkInformation;
```

```csharp
internal static bool IsPortInUse(int port)
{
    try
    {
        var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
        return listeners.Any(l => l.Port == port);
    }
    catch
    {
        return false;
    }
}
```

In `StartAsync()`, **after** the health check fails but **before** starting the server:

```csharp
if (await HealthCheckAsync())
{
    await FetchHealthAsync();
    Log.Information("Server already running (v{Version}), connecting", ServerVersion);
    return;
}

if (IsPortInUse(Port))
{
    Log.Warning("Port {Port} is already in use by another process; the server may fail to start", Port);
}

// ... locate and start server ...
```

### TDD Test Example

```csharp
using System.Net.Sockets;

public class PortConflictTests
{
    [Fact]
    public void IsPortInUse_ReturnsFalse_ForUnusedPort()
    {
        var result = ServerProcessManager.IsPortInUse(51999);
        Assert.False(result);
    }

    [Fact]
    public void IsPortInUse_ReturnsTrue_ForPortWithListener()
    {
        var listener = new TcpListener(IPAddress.Loopback, 51998);
        listener.Start();
        try
        {
            var result = ServerProcessManager.IsPortInUse(51998);
            Assert.True(result);
        }
        finally
        {
            listener.Stop();
        }
    }
}
```

---

## 5. Version Drift Prevention

**Problem**: If Desktop and Server versions diverge (e.g., user updates one but not the other), incompatible behavior may go unnoticed.

**Solution**: Compare major.minor versions after connecting. Log a warning if they differ.

### Implementation

Add to `ServerProcessManager.cs`:

```csharp
public enum VersionDrift { None, Minor, Major, Unknown }

public static VersionDrift CheckVersionDrift(string? desktopVersion, string? serverVersion)
{
    if (desktopVersion == null || serverVersion == null) return VersionDrift.Unknown;

    if (!Version.TryParse(desktopVersion, out var dv) || !Version.TryParse(serverVersion, out var sv))
        return VersionDrift.Unknown;

    if (dv.Major != sv.Major) return VersionDrift.Major;
    if (dv.Minor != sv.Minor) return VersionDrift.Minor;
    return VersionDrift.None;
}
```

In `MainWindow`, after `StartAsync()` completes:

```csharp
await _serverManager.StartAsync();

var drift = ServerProcessManager.CheckVersionDrift(
    _serverManager.GetDesktopVersion(), _serverManager.ServerVersion);
if (drift != ServerProcessManager.VersionDrift.None)
{
    Log.Warning("Version drift detected: {Drift} (Desktop v{Dv}, Server v{Sv})",
        drift, _serverManager.GetDesktopVersion(), _serverManager.ServerVersion);
}
```

### TDD Test Example

```csharp
public class VersionDriftTests
{
    [Fact]
    public void CheckVersionDrift_ReturnsNone_WhenVersionsMatch()
    {
        var drift = ServerProcessManager.CheckVersionDrift("1.3.0", "1.3.0");
        Assert.Equal(ServerProcessManager.VersionDrift.None, drift);
    }

    [Fact]
    public void CheckVersionDrift_ReturnsNone_WhenPatchDiffers()
    {
        var drift = ServerProcessManager.CheckVersionDrift("1.3.0", "1.3.5");
        Assert.Equal(ServerProcessManager.VersionDrift.None, drift);
    }

    [Fact]
    public void CheckVersionDrift_ReturnsMinor_WhenMinorDiffers()
    {
        var drift = ServerProcessManager.CheckVersionDrift("1.3.0", "1.4.0");
        Assert.Equal(ServerProcessManager.VersionDrift.Minor, drift);
    }

    [Fact]
    public void CheckVersionDrift_ReturnsMajor_WhenMajorDiffers()
    {
        var drift = ServerProcessManager.CheckVersionDrift("2.0.0", "1.0.0");
        Assert.Equal(ServerProcessManager.VersionDrift.Major, drift);
    }

    [Fact]
    public void CheckVersionDrift_ReturnsUnknown_WhenNull()
    {
        var drift = ServerProcessManager.CheckVersionDrift(null, "1.3.0");
        Assert.Equal(ServerProcessManager.VersionDrift.Unknown, drift);
    }
}
```

---

## 6. Status Bar

**Problem**: During server startup, the user sees only an empty window with no feedback.

**Solution**: Add a status bar at the bottom with an indeterminate progress bar and status text. Update it at each stage: "Starting server..." → "Connecting to dashboard..." → "Connected".

### MainWindow.axaml

Add a `Border` with `DockPanel.Dock="Bottom"` containing a `ProgressBar` and `TextBlock`:

```xml
<Window ...>
  <DockPanel>
    <Menu DockPanel.Dock="Top">
      <MenuItem Header="_File">
        <MenuItem Header="E_xit" Click="OnExitClick" />
      </MenuItem>
      <MenuItem Header="_Help">
        <MenuItem Header="_About ShipRight Desktop" Click="OnAboutClick" />
      </MenuItem>
    </Menu>

    <Border DockPanel.Dock="Bottom" x:Name="StatusBar" Padding="12,6"
            Background="{DynamicResource SystemControlBackgroundChromeMediumLowBrush}">
      <Grid ColumnDefinitions="Auto,*,Auto">
        <ProgressBar x:Name="StatusProgress" Grid.Column="0" IsIndeterminate="True"
                     Width="120" Height="6" Margin="0,0,12,0" VerticalAlignment="Center" />
        <TextBlock x:Name="StatusText" Grid.Column="1" VerticalAlignment="Center" FontSize="13" />
      </Grid>
    </Border>

    <web:NativeWebView x:Name="Browser" />
  </DockPanel>
</Window>
```

### MainWindow.axaml.cs

Add a helper method and update the `Opened` handler:

```csharp
private void SetStatus(string text, bool showProgress)
{
    StatusText.Text = text;
    StatusProgress.IsVisible = showProgress;
}

// In Opened handler:
Opened += async (_, _) =>
{
    try
    {
        SetStatus("Starting server...", showProgress: true);
        await _serverManager.StartAsync();

        // Optional: version drift check here

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
```

---

## Implementation Order

| Step | What | Test files to create |
|------|------|---------------------|
| 1 | `IWebViewNavigationController`, `WebViewNavigationAdapter`, `IWebView2Probe`, `WebView2Probe` | `DesktopNavigationTests.cs` |
| 2 | Update `MainWindow` to use probe | — |
| 3 | Add catch-block logging + global handlers in `Program.cs` | — |
| 4 | Exponential backoff in `ServerProcessManager` | Add to test file |
| 5 | Port conflict detection | Add to test file |
| 6 | Version drift prevention | Add to test file |
| 7 | Status bar in `MainWindow.axaml` + `SetStatus()` | — |

## Build pipeline

ShipRight's `shipright.iss` (Inno Setup) builds everything from source. Ensure the Desktop publish includes all new `.cs` files. The `InternalsVisibleTo` attribute must be added to the Desktop `.csproj` (not the Server), since all the new code lives in the Desktop project.

Before building the installer, verify **all tests pass**:

```bash
dotnet test back-end/ShipRight.Tests -c Debug
```

---

_For the complete reference implementation, see the SnapTask project at `D:\work\nyingi\code\systems\SnapTask`._
