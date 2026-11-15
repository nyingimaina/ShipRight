using System.Net.Sockets;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Renci.SshNet.Common;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using ShipRight.Modules.ConnectionTest;
using ShipRight.Modules.RemoteHost;
using ShipRight.Shared.SshRunner;

namespace ShipRight.Tests.Modules.ConnectionTest;

[TestClass]
public class ConnectionDiagnosticsTests
{
    private sealed class Capture : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];
        public void Emit(LogEvent e) { lock (Events) Events.Add(e); }
        public string All() { lock (Events) return string.Join("\n", Events.Select(e => e.RenderMessage() + (e.Exception is null ? "" : " EX:" + e.Exception.GetType().Name))); }
    }

    private static (ILogger Log, Capture Sink) Logger()
    {
        var c = new Capture();
        return (new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(c).CreateLogger(), c);
    }

    private sealed class FakeSsh : ISshRunner
    {
        public Exception? Throw { get; set; }
        public int Exit { get; set; }
        public TimeSpan Delay { get; set; }
        public async Task<int> RunAsync(string host, string username, string keyPath, string command,
            Func<string, Task>? onOutput = null, Func<string, Task>? onStderr = null, CancellationToken ct = default)
        {
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
            if (Throw is not null) throw Throw;
            return Exit;
        }
    }

    private sealed class FakeTcp : ITcpProbe
    {
        public TcpProbeResult Result { get; set; } = TcpProbeResult.Success;
        public Task<TcpProbeResult> ProbeAsync(string host, int port, TimeSpan timeout, CancellationToken ct) => Task.FromResult(Result);
    }

    private sealed class FakeMonitoring : IMonitoringProvider
    {
        public SystemMetrics Result { get; set; } = SystemMetrics.Unreachable("boom");
        public Task<SystemMetrics> GetMetricsAsync(RemoteHostConfig host, string keyPath, CancellationToken ct = default) => Task.FromResult(Result);
    }

    // ── NetDiagnostics ────────────────────────────────────────────────────────

    [TestMethod]
    public void DescribeException_walks_inner_exceptions_and_shows_socket_error_codes()
    {
        var ex = new SshConnectionException("Connection failed", new SocketException((int)SocketError.TimedOut));
        var text = NetDiagnostics.DescribeException(ex);
        StringAssert.Contains(text, "SshConnectionException");
        StringAssert.Contains(text, "SocketException");
        StringAssert.Contains(text, "TimedOut");
    }

    [TestMethod]
    public async Task DescribeHost_reports_addresses_for_a_name_that_resolves()
    {
        var text = await NetDiagnostics.DescribeHostAsync("localhost", CancellationToken.None);
        StringAssert.Contains(text, "resolved");
        Assert.IsTrue(text.Contains("127.0.0.1") || text.Contains("::1"), text);
    }

    [TestMethod]
    public async Task DescribeHost_reports_the_failure_for_a_name_that_does_not_resolve()
    {
        var text = await NetDiagnostics.DescribeHostAsync("no-such-host.invalid", CancellationToken.None);
        StringAssert.Contains(text, "DNS lookup failed");
    }

    [TestMethod]
    public async Task DescribeHost_for_a_literal_ip_says_so()
    {
        var text = await NetDiagnostics.DescribeHostAsync("100.64.1.2", CancellationToken.None);
        StringAssert.Contains(text, "100.64.1.2");
    }

    [TestMethod]
    public void DescribeLocalNetwork_names_the_account_and_tailscale_state()
    {
        var text = NetDiagnostics.DescribeLocalNetwork();
        StringAssert.Contains(text, "user=");
        Assert.IsTrue(text.Contains("tailscale", StringComparison.OrdinalIgnoreCase), text);
    }

    [TestMethod]
    public void DescribeKeyFile_shows_size_and_header_but_never_the_key_body_or_path()
    {
        var p = Path.GetTempFileName();
        try
        {
            File.WriteAllText(p, "-----BEGIN OPENSSH PRIVATE KEY-----\nSUPERSECRETBODY\n-----END OPENSSH PRIVATE KEY-----\n");
            var text = NetDiagnostics.DescribeKeyFile(p);
            StringAssert.Contains(text, "BEGIN OPENSSH PRIVATE KEY");
            StringAssert.Contains(text, "bytes=");
            Assert.IsFalse(text.Contains("SUPERSECRETBODY"));
            Assert.IsFalse(text.Contains(p));
        }
        finally { File.Delete(p); }
        StringAssert.Contains(NetDiagnostics.DescribeKeyFile(p), "NOT FOUND");
    }

    [TestMethod]
    public async Task ReadSshBanner_shows_what_the_server_says_when_it_answers()
    {
        var l = new TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        var serve = Task.Run(async () =>
        {
            using var c = await l.AcceptTcpClientAsync();
            await c.GetStream().WriteAsync("SSH-2.0-OpenSSH_9.6p1 Ubuntu\r\n"u8.ToArray());
            await Task.Delay(200);
        });
        try
        {
            var text = await NetDiagnostics.ReadSshBannerAsync("127.0.0.1", port, TimeSpan.FromSeconds(3), CancellationToken.None);
            StringAssert.Contains(text, "SSH-2.0-OpenSSH_9.6p1");
        }
        finally { l.Stop(); await serve; }
    }

    [TestMethod]
    public async Task ReadSshBanner_says_when_it_connects_but_the_server_stays_silent()
    {
        var l = new TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        var serve = Task.Run(async () => { using var c = await l.AcceptTcpClientAsync(); await Task.Delay(1500); });
        try
        {
            var text = await NetDiagnostics.ReadSshBannerAsync("127.0.0.1", port, TimeSpan.FromMilliseconds(400), CancellationToken.None);
            StringAssert.Contains(text, "no banner");
        }
        finally { l.Stop(); await serve; }
    }

    [TestMethod]
    public async Task ReadSshBanner_explains_a_refused_connection()
    {
        var l = new TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start(); var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port; l.Stop();
        var text = await NetDiagnostics.ReadSshBannerAsync("127.0.0.1", port, TimeSpan.FromSeconds(2), CancellationToken.None);
        StringAssert.Contains(text, "banner read failed");
    }

    [TestMethod]
    public async Task DescribeTailscale_never_throws_and_always_names_tailscale()
    {
        var text = await NetDiagnostics.DescribeTailscaleAsync("100.64.1.2", CancellationToken.None);
        Assert.IsTrue(text.Contains("tailscale", StringComparison.OrdinalIgnoreCase), text);
    }

    // ── DiagnosticSshRunner ───────────────────────────────────────────────────

    [TestMethod]
    public async Task Ssh_success_passes_the_exit_code_through_and_logs_timing()
    {
        var (log, sink) = Logger();
        var r = new DiagnosticSshRunner(new FakeSsh { Exit = 3 }, log);
        Assert.AreEqual(3, await r.RunAsync("h", "u", "k", "secret-command-text"));
        var all = sink.All();
        StringAssert.Contains(all, "[ssh-diag]");
        StringAssert.Contains(all, "exit=3");
        Assert.IsFalse(all.Contains("secret-command-text"), "the command text must never be logged");
    }

    [TestMethod]
    public async Task Ssh_failure_is_rethrown_unchanged_and_logged_with_the_full_chain()
    {
        var (log, sink) = Logger();
        var boom = new SshConnectionException("Connection failed", new SocketException((int)SocketError.TimedOut));
        var r = new DiagnosticSshRunner(new FakeSsh { Throw = boom }, log, probe: new FakeTcp { Result = TcpProbeResult.Failed(TcpFailure.TimedOut) });
        var thrown = await Assert.ThrowsExceptionAsync<SshConnectionException>(() => r.RunAsync("localhost", "u", "k", "c"));
        Assert.AreSame(boom, thrown);
        var all = sink.All();
        StringAssert.Contains(all, "SocketException");
        StringAssert.Contains(all, "TimedOut");
        StringAssert.Contains(all, "tcp22=");
        StringAssert.Contains(all, "banner");
        StringAssert.Contains(all, "tailscale");
    }

    [TestMethod]
    public async Task Ssh_authentication_failure_is_not_diagnosed_as_a_network_problem()
    {
        var (log, sink) = Logger();
        var r = new DiagnosticSshRunner(new FakeSsh { Throw = new SshAuthenticationException("denied") }, log, probe: new FakeTcp());
        await Assert.ThrowsExceptionAsync<SshAuthenticationException>(() => r.RunAsync("h", "u", "k", "c"));
        var all = sink.All();
        StringAssert.Contains(all, "SshAuthenticationException");
        Assert.IsFalse(all.Contains("tcp22="), all);
    }

    [TestMethod]
    public async Task Ssh_cancellation_is_logged_and_rethrown()
    {
        var (log, sink) = Logger();
        using var cts = new CancellationTokenSource(50);
        var r = new DiagnosticSshRunner(new FakeSsh { Delay = TimeSpan.FromSeconds(5) }, log);
        await Assert.ThrowsExceptionAsync<TaskCanceledException>(() => r.RunAsync("h", "u", "k", "c", ct: cts.Token));
        StringAssert.Contains(sink.All(), "cancelled");
    }

    // ── DiagnosticTcpProbe ────────────────────────────────────────────────────

    [TestMethod]
    public async Task Tcp_result_is_returned_unchanged_and_failures_are_explained()
    {
        var (log, sink) = Logger();
        var failed = TcpProbeResult.Failed(TcpFailure.NameNotFound);
        var p = new DiagnosticTcpProbe(new FakeTcp { Result = failed }, log);
        var res = await p.ProbeAsync("no-such-host.invalid", 22, TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.AreEqual(failed, res);
        var all = sink.All();
        StringAssert.Contains(all, "NameNotFound");
        StringAssert.Contains(all, "DNS lookup failed");
    }

    [TestMethod]
    public async Task Tcp_success_is_logged_briefly()
    {
        var (log, sink) = Logger();
        var p = new DiagnosticTcpProbe(new FakeTcp(), log);
        Assert.IsTrue((await p.ProbeAsync("h", 22, TimeSpan.FromSeconds(1), CancellationToken.None)).Reached);
        StringAssert.Contains(sink.All(), "reached");
    }

    // ── DiagnosticMonitoringProvider ──────────────────────────────────────────

    [TestMethod]
    public async Task Monitoring_logs_the_error_text_the_screen_will_show()
    {
        var (log, sink) = Logger();
        var m = new DiagnosticMonitoringProvider(new FakeMonitoring(), log);
        var res = await m.GetMetricsAsync(new RemoteHostConfig("box.ts.net", 22, "u"), "k");
        Assert.IsFalse(res.Reachable);
        var all = sink.All();
        StringAssert.Contains(all, "box.ts.net");
        StringAssert.Contains(all, "boom");
    }
}
