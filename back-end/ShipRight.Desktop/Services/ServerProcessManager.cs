using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Text.Json;
using Serilog;
using ShipRight.RuntimeConfig;

namespace ShipRight.Desktop.Services;

public class ServerProcessManager : IDisposable
{
    public enum VersionDrift { None, Minor, Major, Unknown }

    public enum ServerTrust { Compatible, VersionDrift, ExternalHost }

    private readonly AppProfile _profile;
    private Process? _serverProcess;
    private readonly HttpClient _httpClient;
    private bool _disposed;

    public int Port => _profile.Port;

    public string? ServerVersion { get; private set; }
    public string? WebVersion { get; private set; }
    public string? DataDirectory { get; private set; }

    public ServerProcessManager(AppProfile profile)
    {
        _profile = profile;
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{_profile.Port}"),
            Timeout = TimeSpan.FromSeconds(3)
        };
    }

    public async Task StartAsync()
    {
        if (await HealthCheckAsync())
        {
            await FetchServerHealthAsync();
            var trust = EvaluateExistingServer(GetDesktopVersion(), ServerVersion, DataDirectory);
            if (trust == ServerTrust.Compatible)
            {
                Log.Information("Server already running (v{Version}), connecting", ServerVersion);
                return;
            }

            throw new InvalidOperationException(
                BuildConflictMessage(trust, GetDesktopVersion(), ServerVersion, WebVersion, DataDirectory, Port));
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
        startInfo.ArgumentList.Add($"--port={_profile.Port}");
        startInfo.ArgumentList.Add($"--data-dir={_profile.DataDirectory}");

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

        await FetchServerHealthAsync();
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

    private async Task FetchServerHealthAsync()
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
            if (root.TryGetProperty("dataDirectory", out var dd))
                DataDirectory = dd.GetString();
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

    internal static ServerTrust EvaluateExistingServer(string? desktopVersion, string? serverVersion, string? dataDirectory)
    {
        if (string.IsNullOrWhiteSpace(serverVersion) || string.IsNullOrWhiteSpace(dataDirectory))
            return ServerTrust.ExternalHost;

        if (!IsWindowsDataDirectory(dataDirectory))
            return ServerTrust.ExternalHost;

        return CheckVersionDrift(desktopVersion, serverVersion) switch
        {
            VersionDrift.None => ServerTrust.Compatible,
            VersionDrift.Unknown => ServerTrust.ExternalHost,
            _ => ServerTrust.VersionDrift,
        };
    }

    private static bool IsWindowsDataDirectory(string dataDirectory)
    {
        if (dataDirectory.StartsWith("/", StringComparison.Ordinal))
            return false;

        if (dataDirectory.IndexOf("wsl.localhost", StringComparison.OrdinalIgnoreCase) >= 0)
            return false;

        if (dataDirectory.Length >= 2 && dataDirectory[1] == ':' && char.IsLetter(dataDirectory[0]))
            return true;

        return dataDirectory.StartsWith(@"\\", StringComparison.Ordinal);
    }

    internal static string BuildConflictMessage(
        ServerTrust trust, string? desktopVersion, string? serverVersion, string? webVersion, string? dataDirectory,
        int port = AppProfile.DefaultPort)
    {
        return trust switch
        {
            ServerTrust.ExternalHost =>
                $"Port {port} is occupied by another ShipRight instance (server v{serverVersion}, " +
                $"front-end v{webVersion}, data at {dataDirectory}) that is not this installation. " +
                "Stop that instance (e.g. a stale server inside WSL/Docker) and retry.",
            ServerTrust.VersionDrift =>
                $"Port {port} is occupied by an older ShipRight server (v{serverVersion}) but this app is " +
                $"v{desktopVersion}. Stop the old server and retry.",
            _ => $"Port {port} is occupied by another process. Free it and retry.",
        };
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
