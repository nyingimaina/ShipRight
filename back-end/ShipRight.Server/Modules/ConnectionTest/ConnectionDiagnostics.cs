using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Renci.SshNet.Common;
using Serilog;
using ILogger = Serilog.ILogger;
using ShipRight.Modules.RemoteHost;
using ShipRight.Shared.SshRunner;

namespace ShipRight.Modules.ConnectionTest;

/// <summary>
/// Facts that tell a Tailscale / SSH failure apart: the whole exception chain, what DNS says, and what this computer's
/// network looks like. Every line is for the log only; none of it changes behaviour. Never includes commands or key paths.
/// </summary>
public static class NetDiagnostics
{
    public static string DescribeException(Exception ex)
    {
        var parts = new List<string>();
        for (Exception? e = ex; e is not null && parts.Count < 8; e = e.InnerException)
        {
            var extra = e is SocketException se ? $" SocketError={se.SocketErrorCode}({se.NativeErrorCode})" : "";
            parts.Add($"{e.GetType().Name}{extra} HResult=0x{e.HResult:X8}: {e.Message}");
        }
        return string.Join(" <- ", parts);
    }

    public static async Task<string> DescribeHostAsync(string host, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            if (IPAddress.TryParse(host, out var literal))
                return $"host {host} is a literal IP ({literal.AddressFamily}); no DNS needed";
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(5));
            var addrs = await Dns.GetHostAddressesAsync(host, limit.Token);
            return $"host {host} resolved to [{string.Join(", ", addrs.Select(a => a.ToString()))}] in {sw.ElapsedMilliseconds}ms";
        }
        catch (Exception ex)
        {
            return $"DNS lookup failed for {host} after {sw.ElapsedMilliseconds}ms: {DescribeException(ex)}";
        }
    }

    /// <summary>Opens a raw socket to the SSH port and reads the server's identification line (no login). Shows whether sshd answers through this route and which version it is.</summary>
    public static async Task<string> ReadSshBannerAsync(string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, limit.Token);
            var connectMs = sw.ElapsedMilliseconds;
            var remote = client.Client.RemoteEndPoint?.ToString() ?? "?";
            var buf = new byte[255];
            var n = 0;
            var stream = client.GetStream();
            try
            {
                while (n < buf.Length)
                {
                    var read = await stream.ReadAsync(buf.AsMemory(n), limit.Token);
                    if (read == 0) break;
                    n += read;
                    if (Array.IndexOf(buf, (byte)'\n', 0, n) >= 0) break;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            if (n == 0) return $"connected to {remote} in {connectMs}ms but no banner within {(int)timeout.TotalMilliseconds}ms (server accepted the connection and said nothing)";
            var line = global::System.Text.Encoding.ASCII.GetString(buf, 0, n).Split('\n')[0].Trim();
            return $"banner from {remote}: '{line}' (connect {connectMs}ms, banner {sw.ElapsedMilliseconds}ms)";
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return $"banner read failed after {sw.ElapsedMilliseconds}ms: {DescribeException(ex)}";
        }
    }

    /// <summary>This computer's own view of the tailnet: the `tailscale status` line(s) for the host (online? direct or relayed? last seen?) plus the overall state.</summary>
    public static async Task<string> DescribeTailscaleAsync(string host, CancellationToken ct)
    {
        try
        {
            var exe = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Tailscale", "tailscale.exe"),
            }.FirstOrDefault(File.Exists) ?? "tailscale";
            using var p = Process.Start(new ProcessStartInfo(exe, "status")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            })!;
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(5));
            var outTask = p.StandardOutput.ReadToEndAsync(limit.Token);
            var errTask = p.StandardError.ReadToEndAsync(limit.Token);
            await p.WaitForExitAsync(limit.Token);
            var output = (await outTask).Trim();
            var err = (await errTask).Trim();
            if (p.ExitCode != 0 || output.Length == 0)
                return $"tailscale status exit={p.ExitCode}: {(err.Length > 0 ? err : output)}";
            var shortName = host.Split('.')[0];
            var lines = output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            var mine = lines.Where(l => l.Contains(host, StringComparison.OrdinalIgnoreCase) || l.Contains(shortName, StringComparison.OrdinalIgnoreCase)).Take(3).ToList();
            var first = lines.Count > 0 ? lines[0] : "";
            return mine.Count > 0
                ? $"tailscale status ({lines.Count} lines): {string.Join(" | ", mine)}"
                : $"tailscale status ({lines.Count} lines) has NO entry matching '{host}'; first line: {first}";
        }
        catch (Exception ex)
        {
            return $"tailscale CLI not usable here ({ex.GetType().Name}: {ex.Message})";
        }
    }

    /// <summary>Key file facts safe to log: exists, size, and the public BEGIN header line only. Never the path or the key body.</summary>
    public static string DescribeKeyFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "key: no path set";
        try
        {
            if (!File.Exists(path)) return "key: file NOT FOUND";
            var info = new FileInfo(path);
            using var r = new StreamReader(path);
            var first = (r.ReadLine() ?? "").Trim();
            var header = first.StartsWith("-----BEGIN", StringComparison.Ordinal) && first.Length < 60 ? first : "(not a PEM header)";
            return $"key: exists bytes={info.Length} header={header} modified={info.LastWriteTimeUtc:u}";
        }
        catch (Exception ex)
        {
            return $"key: unreadable ({ex.GetType().Name})";
        }
    }

    public static string DescribeLocalNetwork()
    {
        string user;
        try { user = $"{Environment.UserDomainName}\\{Environment.UserName}"; } catch { user = "?"; }
        try
        {
            var found = NetworkInterface.GetAllNetworkInterfaces()
                .Select(n => new
                {
                    n.Name,
                    n.Description,
                    n.OperationalStatus,
                    Ips = n.GetIPProperties().UnicastAddresses.Select(a => a.Address).Where(IsTailnetIp).ToList(),
                })
                .Where(n => n.Ips.Count > 0 ||
                            n.Name.Contains("tailscale", StringComparison.OrdinalIgnoreCase) ||
                            n.Description.Contains("tailscale", StringComparison.OrdinalIgnoreCase))
                .Select(n => $"{n.Name} ({n.OperationalStatus}) [{string.Join(", ", n.Ips)}]")
                .ToList();
            var tail = found.Count == 0 ? "no tailscale interface found on this computer" : "tailscale interface: " + string.Join("; ", found);
            return $"user={user}; {tail}";
        }
        catch (Exception ex)
        {
            return $"user={user}; tailscale interface check failed: {ex.GetType().Name}: {ex.Message}";
        }
    }

    private static bool IsTailnetIp(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        return b[0] == 100 && b[1] is >= 64 and <= 127;
    }
}

/// <summary>Logs every SSH call (timing, exit code) and, when one fails, the full error chain plus a DNS / port-22 / local-network snapshot.</summary>
public sealed class DiagnosticSshRunner(ISshRunner inner, ILogger? log = null, ITcpProbe? probe = null) : ISshRunner
{
    private readonly ILogger _log = log ?? Serilog.Log.Logger;
    private readonly ITcpProbe _probe = probe ?? new TcpProbe();

    public async Task<int> RunAsync(string host, string username, string keyPath, string command,
        Func<string, Task>? onOutput = null, Func<string, Task>? onStderr = null, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        _log.Information("[ssh-diag] start {User}@{Host} {Key} commandChars={Chars} sshNet={SshNet}", username, host,
            NetDiagnostics.DescribeKeyFile(keyPath), command?.Length ?? 0, typeof(Renci.SshNet.SshClient).Assembly.GetName().Version);
        try
        {
            var exit = await inner.RunAsync(host, username, keyPath, command!, onOutput, onStderr, ct);
            _log.Information("[ssh-diag] done {User}@{Host} exit={Exit} elapsedMs={Ms}", username, host, exit, sw.ElapsedMilliseconds);
            return exit;
        }
        catch (OperationCanceledException)
        {
            _log.Warning("[ssh-diag] cancelled {User}@{Host} after {Ms}ms (callerCancelled={Caller})", username, host, sw.ElapsedMilliseconds, ct.IsCancellationRequested);
            throw;
        }
        catch (Exception ex)
        {
            var chain = NetDiagnostics.DescribeException(ex);
            var network = IsNetworkShaped(ex) ? await SnapshotAsync(host) : "not a network error, snapshot skipped";
            _log.Warning(ex, "[ssh-diag] FAILED {User}@{Host} after {Ms}ms. chain: {Chain}. {Network}", username, host, sw.ElapsedMilliseconds, chain, network);
            throw;
        }
    }

    private static bool IsNetworkShaped(Exception ex) =>
        ex is SocketException or SshConnectionException or SshOperationTimeoutException or TimeoutException
        || ex.InnerException is not null && IsNetworkShaped(ex.InnerException);

    private async Task<string> SnapshotAsync(string host)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            var dns = NetDiagnostics.DescribeHostAsync(host, cts.Token);
            var ts = NetDiagnostics.DescribeTailscaleAsync(host, cts.Token);
            var banner = NetDiagnostics.ReadSshBannerAsync(host, 22, TimeSpan.FromSeconds(4), cts.Token);
            var sw = Stopwatch.StartNew();
            var tcp = await _probe.ProbeAsync(host, 22, TimeSpan.FromSeconds(4), cts.Token);
            return $"tcp22={(tcp.Reached ? "reached" : tcp.Failure.ToString())} in {sw.ElapsedMilliseconds}ms; {await dns}; {await banner}; {await ts}; local: {NetDiagnostics.DescribeLocalNetwork()}";
        }
        catch (Exception e)
        {
            return $"snapshot failed: {e.GetType().Name}: {e.Message}";
        }
    }
}

/// <summary>Logs each port-22 reachability probe; on failure adds the DNS answer and this computer's Tailscale state.</summary>
public sealed class DiagnosticTcpProbe(ITcpProbe inner, ILogger? log = null) : ITcpProbe
{
    private readonly ILogger _log = log ?? Serilog.Log.Logger;

    public async Task<TcpProbeResult> ProbeAsync(string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var res = await inner.ProbeAsync(host, port, timeout, ct);
        if (res.Reached)
        {
            _log.Information("[tcp-diag] reached {Host}:{Port} in {Ms}ms", host, port, sw.ElapsedMilliseconds);
            return res;
        }
        var dns = await NetDiagnostics.DescribeHostAsync(host, CancellationToken.None);
        var ts = await NetDiagnostics.DescribeTailscaleAsync(host, CancellationToken.None);
        _log.Warning("[tcp-diag] {Failure} for {Host}:{Port} after {Ms}ms (limit {Limit}s). {Dns}; {Ts}; local: {Local}",
            res.Failure, host, port, sw.ElapsedMilliseconds, (int)timeout.TotalSeconds, dns, ts, NetDiagnostics.DescribeLocalNetwork());
        return res;
    }
}

/// <summary>Logs what the Monitoring screen is about to show for each server: reachability, the exact error text and how long it took.</summary>
public sealed class DiagnosticMonitoringProvider(IMonitoringProvider inner, ILogger? log = null) : IMonitoringProvider
{
    private readonly ILogger _log = log ?? Serilog.Log.Logger;

    public async Task<SystemMetrics> GetMetricsAsync(RemoteHostConfig host, string keyPath, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var m = await inner.GetMetricsAsync(host, keyPath, ct);
        if (m.Reachable) _log.Information("[monitor-diag] {Host} ok in {Ms}ms", host.Host, sw.ElapsedMilliseconds);
        else _log.Warning("[monitor-diag] {Host} UNREACHABLE after {Ms}ms; error shown to user: {Error}", host.Host, sw.ElapsedMilliseconds, m.Error);
        return m;
    }
}
