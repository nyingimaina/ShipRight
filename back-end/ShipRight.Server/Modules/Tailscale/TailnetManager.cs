using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Serilog;
using ShipRight.Modules.ServerNetwork;
using ShipRight.Shared.Store;

namespace ShipRight.Modules.Tailscale;

/// <summary>
/// Runs one unprivileged tailscaled userspace node per tenant tailnet and exposes its loopback SOCKS5
/// endpoint. It owns only Tailscale process/state details; routing and SSH live outside this class.
/// </summary>
public sealed class TailnetManager : ITailnetManager, IDisposable
{
    private readonly ITailnetStore _store;
    private readonly TailscaleOAuthKeyMinter _oauth;
    private readonly Func<string, string?> _getEnv;
    private readonly string? _dataDir;
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    private sealed class Session(string id, TailnetConnection connection)
    {
        public string Id { get; } = id;
        public TailnetConnection Connection { get; } = connection;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public Process? Process { get; set; }
        public int SocksPort { get; set; }
        public string SocketPath { get; set; } = string.Empty;
        public string StateFile { get; set; } = string.Empty;
        public bool Online { get; set; }
    }

    public TailnetManager(
        ITailnetStore store,
        TailscaleOAuthKeyMinter oauth,
        Func<string, string?>? getEnv = null,
        string? dataDir = null)
    {
        _store = store;
        _oauth = oauth;
        _getEnv = getEnv ?? Environment.GetEnvironmentVariable;
        _dataDir = dataDir;
    }

    public async Task<TailnetRuntime> EnsureRunningAsync(string tailnetId, CancellationToken ct = default)
    {
        var connection = await _store.GetByIdAsync(tailnetId)
            ?? throw new NetworkUnavailableException($"Tailscale network '{tailnetId}' was not found.");

        var session = SessionFor(connection);
        await session.Gate.WaitAsync(ct);
        try
        {
            if (session.Process is { HasExited: false } && session.Online)
                return Runtime(session, true);

            await StartAsync(session, ct);
            for (var i = 0; i < 10 && !session.Online; i++)
            {
                session.Online = await IsOnlineAsync(session, ct);
                if (!session.Online) await Task.Delay(250, ct);
            }
            if (!session.Online)
                throw new NetworkUnavailableException($"Tailscale network '{connection.Name}' did not come online.");

            return Runtime(session, true);
        }
        finally
        {
            session.Gate.Release();
        }
    }

    public async Task<TailnetRuntime> GetStatusAsync(string tailnetId, CancellationToken ct = default)
    {
        var connection = await _store.GetByIdAsync(tailnetId)
            ?? throw new NetworkUnavailableException($"Tailscale network '{tailnetId}' was not found.");

        var session = SessionFor(connection);
        await session.Gate.WaitAsync(ct);
        try
        {
            var running = session.Process is { HasExited: false };
            if (running) session.Online = await IsOnlineAsync(session, ct);
            return Runtime(session, running && session.Online);
        }
        finally
        {
            session.Gate.Release();
        }
    }

    public async Task StopAsync(string tailnetId, CancellationToken ct = default)
    {
        Session? session;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(tailnetId, out session)) return;
            _sessions.Remove(tailnetId);
        }

        await session.Gate.WaitAsync(ct);
        try
        {
            Kill(session);
        }
        finally
        {
            session.Gate.Release();
            session.Gate.Dispose();
        }
    }

    /// <summary>Stops the node and deletes the state ShipRight kept for it, so a removed network leaves nothing behind.</summary>
    public async Task RemoveAsync(string tailnetId, CancellationToken ct = default)
    {
        await StopAsync(tailnetId, ct);

        try
        {
            var companies = Path.Combine(_dataDir ?? DataDirectory.Resolve(), "tailscale");
            if (!Directory.Exists(companies)) return;
            foreach (var dir in Directory.GetDirectories(companies, tailnetId, SearchOption.AllDirectories))
            {
                Directory.Delete(dir, recursive: true);
                Log.Information("Removed Tailscale state for {TailnetId}", tailnetId);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not remove Tailscale state for {TailnetId}", tailnetId);
        }
    }

    private Session SessionFor(TailnetConnection connection)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(connection.Id, out var session))
            {
                session = new Session(connection.Id, connection);
                _sessions[connection.Id] = session;
            }
            return session;
        }
    }

    private async Task StartAsync(Session session, CancellationToken ct)
    {
        Kill(session);

        var root = Path.Combine(_dataDir ?? DataDirectory.Resolve(), "tailscale", session.Connection.CompanyId.ToString("N"), session.Id);
        Directory.CreateDirectory(root);
        // Short file names: a Unix domain socket path must fit in 108 characters on Linux.
        session.SocketPath = Path.Combine(root, "ts.sock");
        session.StateFile = Path.Combine(root, "ts.state");
        session.SocksPort = FreeLoopbackPort();

        var daemon = _getEnv("TAILSCALED_PATH");
        daemon = string.IsNullOrWhiteSpace(daemon) ? "tailscaled" : daemon;
        var start = new ProcessStartInfo
        {
            FileName = daemon,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("--tun=userspace-networking");
        start.ArgumentList.Add($"--socks5-server=127.0.0.1:{session.SocksPort}");
        start.ArgumentList.Add($"--state={session.StateFile}");
        start.ArgumentList.Add($"--socket={session.SocketPath}");

        session.Process = Process.Start(start)
            ?? throw new NetworkUnavailableException("tailscaled could not start.");
        session.Process.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log.Debug("tailscaled: {Line}", e.Data); };
        session.Process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log.Warning("tailscaled: {Line}", e.Data); };
        session.Process.BeginOutputReadLine();
        session.Process.BeginErrorReadLine();

        await WaitForSocketAsync(session, ct);

        // A node that is already signed in keeps its identity in the state file: reuse it rather than
        // spending another (often single-use, short-lived) auth key.
        var (backend, online) = await ReadStatusAsync(session, ct);
        if (online)
        {
            session.Online = true;
            return;
        }

        var needsKey = string.IsNullOrEmpty(backend) ||
                       backend.Equals("NeedsLogin", StringComparison.OrdinalIgnoreCase);
        var key = string.Empty;
        string? keyApiFailure = null;
        if (needsKey)
        {
            key = session.Connection.AuthKey;
            if (string.IsNullOrWhiteSpace(key))
            {
                try
                {
                    key = await _oauth.CreateAuthKeyAsync(session.Connection, ct);
                }
                catch (NetworkUnavailableException ex) when (HasOAuthClient(session.Connection))
                {
                    // Tailscale documents using the trust credential's secret directly in `tailscale up` when the
                    // key API is unavailable or not permitted. Tags are supplied with --advertise-tags below.
                    keyApiFailure = ex.Message;
                    key = $"{session.Connection.OAuthClientSecret.Trim()}?ephemeral=true&preauthorized=true";
                    Log.Warning("Tailscale key API failed; trying the trust credential secret directly: {Reason}", ex.Message);
                }
            }
        }

        var hostname = $"shipright-{session.Id[..Math.Min(8, session.Id.Length)]}";
        var args = new List<string> { "up", $"--hostname={hostname}" };
        if (!string.IsNullOrWhiteSpace(key))
            args.Add($"--auth-key={key}");
        if (!string.IsNullOrWhiteSpace(session.Connection.Tags))
            args.Add($"--advertise-tags={session.Connection.Tags}");

        var result = await RunTailscaleAsync(session, args, ct);
        if (result.ExitCode != 0)
        {
            var message = $"Tailscale could not join '{session.Connection.Name}': {result.Error.Trim()}";
            if (keyApiFailure is not null)
                message += $" (The key API also failed: {keyApiFailure})";
            throw new NetworkUnavailableException(message);
        }
    }

    private static bool HasOAuthClient(TailnetConnection connection) =>
        !string.IsNullOrWhiteSpace(connection.OAuthClientId) &&
        !string.IsNullOrWhiteSpace(connection.OAuthClientSecret);

    private async Task<bool> IsOnlineAsync(Session session, CancellationToken ct) =>
        (await ReadStatusAsync(session, ct)).Online;

    /// <summary>The daemon's backend state and whether this node is online. A failed read is (null, false).</summary>
    private async Task<(string? Backend, bool Online)> ReadStatusAsync(Session session, CancellationToken ct)
    {
        var result = await RunTailscaleAsync(session, ["status", "--json"], ct);
        if (result.ExitCode != 0) return (null, false);

        try
        {
            using var doc = JsonDocument.Parse(result.Output);
            var root = doc.RootElement;
            var backend = root.TryGetProperty("BackendState", out var b) ? b.GetString() : null;
            var online = root.TryGetProperty("Self", out var self) &&
                         self.TryGetProperty("Online", out var o) && o.ValueKind == JsonValueKind.True;
            return (backend, online && string.Equals(backend, "Running", StringComparison.OrdinalIgnoreCase));
        }
        catch (JsonException)
        {
            return (null, false);
        }
    }

    private static TailnetRuntime Runtime(Session session, bool online) =>
        new(session.Id, session.Connection.Name, session.SocksPort, online);

    private async Task WaitForSocketAsync(Session session, CancellationToken ct)
    {
        for (var i = 0; i < 150; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(session.SocketPath)) return;
            if (session.Process is null || session.Process.HasExited)
                throw new NetworkUnavailableException("tailscaled stopped before it was ready.");
            await Task.Delay(100, ct);
        }

        throw new NetworkUnavailableException("tailscaled did not become ready in time.");
    }

    private async Task<(int ExitCode, string Output, string Error)> RunTailscaleAsync(
        Session session, IReadOnlyList<string> args, CancellationToken ct)
    {
        var cli = _getEnv("TAILSCALE_PATH");
        cli = string.IsNullOrWhiteSpace(cli) ? "tailscale" : cli;
        var start = new ProcessStartInfo
        {
            FileName = cli,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add($"--socket={session.SocketPath}");
        foreach (var arg in args) start.ArgumentList.Add(arg);

        using var process = Process.Start(start)
            ?? throw new NetworkUnavailableException("tailscale CLI could not start.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            throw new NetworkUnavailableException("Tailscale CLI did not finish in time.");
        }
        return (process.ExitCode, await output, await error);
    }

    private static int FreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static void Kill(Session session)
    {
        try
        {
            if (session.Process is { HasExited: false }) session.Process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not stop tailscaled for {TailnetId}", session.Id);
        }
        finally
        {
            session.Process?.Dispose();
            session.Process = null;
            session.Online = false;
        }
    }

    public void Dispose()
    {
        Session[] sessions;
        lock (_gate) { sessions = _sessions.Values.ToArray(); _sessions.Clear(); }
        foreach (var session in sessions) Kill(session);
    }
}

public sealed class TailscaleOAuthKeyMinter(HttpClient? http = null)
{
    private readonly HttpClient _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

    public async Task<string> CreateAuthKeyAsync(TailnetConnection connection, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(connection.OAuthClientId) || string.IsNullOrWhiteSpace(connection.OAuthClientSecret))
            throw new NetworkUnavailableException(
                "This Tailscale network has no auth key or OAuth client. Add one in Tailscale settings.");

        using var tokenRequest = new HttpRequestMessage(HttpMethod.Post, "https://api.tailscale.com/api/v2/oauth/token");
        tokenRequest.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = connection.OAuthClientId,
            ["client_secret"] = connection.OAuthClientSecret,
            ["grant_type"] = "client_credentials",
        });

        using var tokenResponse = await _http.SendAsync(tokenRequest, ct);
        var tokenBody = await tokenResponse.Content.ReadAsStringAsync(ct);
        if (!tokenResponse.IsSuccessStatusCode)
            throw new NetworkUnavailableException($"Tailscale OAuth token request failed: {tokenBody}");

        using var tokenDoc = JsonDocument.Parse(tokenBody);
        var accessToken = tokenDoc.RootElement.GetProperty("access_token").GetString()
            ?? throw new NetworkUnavailableException("Tailscale OAuth token response had no access token.");

        var configured = string.IsNullOrWhiteSpace(connection.Tailnet) ? "-" : connection.Tailnet.Trim();
        var candidates = configured == "-" ? new[] { "-" } : new[] { configured, "-" };
        var tags = string.IsNullOrWhiteSpace(connection.Tags)
            ? Array.Empty<string>()
            : connection.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        HttpStatusCode? lastStatus = null;
        string? lastBody = null;
        foreach (var tailnet in candidates)
        {
            using var keyRequest = BuildKeyRequest(accessToken, tailnet, tags);
            using var keyResponse = await _http.SendAsync(keyRequest, ct);
            var keyBody = await keyResponse.Content.ReadAsStringAsync(ct);

            if (keyResponse.IsSuccessStatusCode)
            {
                using var keyDoc = JsonDocument.Parse(keyBody);
                return keyDoc.RootElement.GetProperty("key").GetString()
                    ?? throw new NetworkUnavailableException("Tailscale auth key response had no key.");
            }

            lastStatus = keyResponse.StatusCode;
            lastBody = keyBody;

            if (keyResponse.StatusCode != HttpStatusCode.NotFound) break;
            if (tailnet != "-")
                Log.Warning("Tailscale tailnet '{Tailnet}' was not found; retrying with the credential's default tailnet", configured);
        }

        throw new NetworkUnavailableException(DescribeKeyFailure(lastStatus, configured, lastBody));
    }

    private static HttpRequestMessage BuildKeyRequest(string accessToken, string tailnet, string[] tags)
    {
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://api.tailscale.com/api/v2/tailnet/{Uri.EscapeDataString(tailnet)}/keys");
        request.Headers.Authorization = new global::System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = JsonContent.Create(new
        {
            capabilities = new
            {
                devices = new
                {
                    create = new
                    {
                        reusable = false,
                        ephemeral = true,
                        preauthorized = true,
                        tags,
                    },
                },
            },
            expirySeconds = 3600,
        });
        return request;
    }

    private static string DescribeKeyFailure(HttpStatusCode? status, string configuredTailnet, string? body)
    {
        var detail = string.IsNullOrWhiteSpace(body) ? string.Empty : $" Tailscale said: {body.Trim()}";
        return status switch
        {
            HttpStatusCode.NotFound =>
                $"Tailscale could not find the tailnet '{configuredTailnet}'. Use the tailnet name from " +
                "Admin → Settings → General (for a solo plan it is the email you signed up with), or set " +
                "Tailnet to '-' to use the trust credential's default tailnet. Also confirm the trust " +
                $"credential has the 'Auth keys' scope with Write access.{detail}",
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                "Tailscale rejected the trust credential. Give it the 'Auth keys' scope with Write access " +
                $"and make sure it belongs to this tailnet.{detail}",
            _ => $"Tailscale could not create an auth key ({status?.ToString() ?? "no response"}).{detail}",
        };
    }
}
