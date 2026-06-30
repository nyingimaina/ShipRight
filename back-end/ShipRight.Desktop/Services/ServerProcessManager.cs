using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Text.Json;
using Serilog;

namespace ShipRight.Desktop.Services;

public class ServerProcessManager : IDisposable
{
    public enum VersionDrift { None, Minor, Major, Unknown }

    private Process? _serverProcess;
    private readonly HttpClient _httpClient;
    private bool _disposed;

    public const int Port = 5200;

    public string? ServerVersion { get; private set; }
    public string? WebVersion { get; private set; }

    public ServerProcessManager()
    {
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{Port}"),
            Timeout = TimeSpan.FromSeconds(3)
        };
    }

    public async Task StartAsync()
    {
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

        var serverPath = LocateServerBinary();
        if (serverPath == null)
        {
            throw new FileNotFoundException(
                "ShipRight.Server.exe not found. Ensure it is installed alongside ShipRight.Desktop.exe.");
        }

        var startInfo = new ProcessStartInfo(serverPath)
        {
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(serverPath)
        };

        _serverProcess = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };

        _serverProcess.Exited += (_, _) =>
        {
            Log.Warning("Server process exited unexpectedly (code: {Code})",
                _serverProcess?.ExitCode ?? -1);
        };

        _serverProcess.Start();
        Log.Information("Server process started (PID: {Pid})", _serverProcess.Id);

        if (!await PollHealthAsync())
        {
            throw new TimeoutException("Server failed to become ready within the expected time.");
        }

        await FetchHealthAsync();
        Log.Information("Server ready (v{Version}, PID: {Pid})", ServerVersion, _serverProcess.Id);
    }

    public async Task ShutdownAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var response = await _httpClient.PostAsync("/api/system/shutdown", null, cts.Token);
            if (response.IsSuccessStatusCode)
            {
                Log.Information("Graceful shutdown request sent");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Graceful shutdown failed");
        }

        if (_serverProcess != null)
        {
            if (!_serverProcess.WaitForExit(5000))
            {
                Log.Warning("Server did not exit in time, force killing");
                _serverProcess.Kill();
                _serverProcess.WaitForExit(2000);
            }
            Log.Information("Server process exited");
        }
    }

    private async Task<bool> HealthCheckAsync()
    {
        try
        {
            var response = await _httpClient.GetAsync("/api/health");
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Health check failed before server started");
            return false;
        }
    }

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

    internal static async Task<bool> PollWithExponentialBackoffAsync(Func<Task<bool>> check, int maxRetries = 10)
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

    private async Task FetchHealthAsync()
    {
        try
        {
            var response = await _httpClient.GetAsync("/api/health");
            if (!response.IsSuccessStatusCode) return;

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("serverVersion", out var sv))
                ServerVersion = sv.GetString() ?? "unknown";
            if (root.TryGetProperty("webVersion", out var wv))
                WebVersion = wv.GetString();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to fetch health info from server");
        }
    }

    public string GetDesktopVersion()
    {
        var version = Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "0.0.0";
        var plusIdx = version.IndexOf('+');
        return plusIdx >= 0 ? version[..plusIdx] : version;
    }

    public static VersionDrift CheckVersionDrift(string? desktopVersion, string? serverVersion)
    {
        if (desktopVersion == null || serverVersion == null) return VersionDrift.Unknown;

        if (!Version.TryParse(desktopVersion, out var dv) || !Version.TryParse(serverVersion, out var sv))
            return VersionDrift.Unknown;

        if (dv.Major != sv.Major) return VersionDrift.Major;
        if (dv.Minor != sv.Minor) return VersionDrift.Minor;
        return VersionDrift.None;
    }

    private static string? LocateServerBinary()
    {
        var baseDir = AppContext.BaseDirectory;

        var candidates = new[]
        {
            Path.Combine(baseDir, "ShipRight.Server.exe"),
            Path.Combine(baseDir, "ShipRight.Server"),
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _httpClient.Dispose();
        _serverProcess?.Dispose();
    }
}
