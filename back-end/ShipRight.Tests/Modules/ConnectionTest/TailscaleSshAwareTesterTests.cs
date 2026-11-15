using Microsoft.VisualStudio.TestTools.UnitTesting;
using Renci.SshNet.Common;
using ShipRight.Modules.ConnectionTest;
using ShipRight.Modules.Projects;
using ShipRight.Shared.SshRunner;

namespace ShipRight.Tests.Modules.ConnectionTest;

[TestClass]
public class TailscaleSshAwareTesterTests
{
    private sealed class FakeSsh(Exception? toThrow) : ISshRunner
    {
        public Task<int> RunAsync(string host, string username, string keyPath, string command,
            Func<string, Task>? onOutput = null, Func<string, Task>? onStderr = null, CancellationToken ct = default)
            => toThrow is null ? Task.FromResult(0) : throw toThrow;
    }

    private sealed class ReachableTcp : ITcpProbe
    {
        public Task<TcpProbeResult> ProbeAsync(string host, int port, TimeSpan timeout, CancellationToken ct) => Task.FromResult(TcpProbeResult.Success);
    }

    private static ServerConfig Server() =>
        new() { Id = "s1", Name = "box", Host = "100.121.70.4", Username = "ubuntu", SshKeyPath = "/k.pem", RemoteWorkingDir = "/home/ubuntu/x" };

    private static TailscaleSshAwareTester Tester(Exception? sshFails, string banner) =>
        new(new ConnectionTester(new FakeSsh(sshFails), new ReachableTcp()), (_, _) => Task.FromResult(banner));

    [TestMethod]
    public void Recognises_the_tailscale_ssh_banner()
    {
        Assert.IsTrue(TailscaleSshAwareTester.IsTailscaleBanner("banner from [::ffff:100.121.70.4]:22: 'SSH-2.0-Tailscale' (connect 184ms)"));
        Assert.IsFalse(TailscaleSshAwareTester.IsTailscaleBanner("banner from 1.2.3.4:22: 'SSH-2.0-OpenSSH_9.6p1 Ubuntu'"));
        Assert.IsFalse(TailscaleSshAwareTester.IsTailscaleBanner("connected but no banner within 4000ms"));
    }

    [TestMethod]
    public async Task A_timed_out_handshake_with_a_tailscale_banner_gets_a_specific_explanation_and_command()
    {
        var r = await Tester(new SshOperationTimeoutException("Session operation has timed out"), "banner: 'SSH-2.0-Tailscale'").TestAsync(Server());
        Assert.IsFalse(r.Ready);
        var fail = r.Items.First(i => i.Status == CheckStatus.Fail);
        StringAssert.Contains(fail.Message, "Tailscale SSH");
        Assert.AreEqual("sudo tailscale set --ssh=false", fail.Command);
        StringAssert.Contains(fail.Fix!, "accept");
        StringAssert.Contains(r.Summary, "Tailscale SSH");
    }

    [TestMethod]
    public async Task The_same_failure_with_a_normal_openssh_banner_is_left_alone()
    {
        var plain = await new ConnectionTester(new FakeSsh(new SshOperationTimeoutException("x")), new ReachableTcp()).TestAsync(Server());
        var r = await Tester(new SshOperationTimeoutException("x"), "banner: 'SSH-2.0-OpenSSH_9.6'").TestAsync(Server());
        CollectionAssert.AreEqual(plain.Items.Select(i => i.Message).ToList(), r.Items.Select(i => i.Message).ToList());
    }

    [TestMethod]
    public async Task A_successful_test_never_reads_the_banner()
    {
        var called = false;
        var t = new TailscaleSshAwareTester(new ConnectionTester(new FakeSsh(null), new ReachableTcp()), (_, _) => { called = true; return Task.FromResult("'SSH-2.0-Tailscale'"); });
        var r = await t.TestAsync(Server());
        Assert.IsFalse(called);
        Assert.IsNotNull(r);
    }

    [TestMethod]
    public async Task A_banner_lookup_that_throws_leaves_the_original_result()
    {
        var t = new TailscaleSshAwareTester(new ConnectionTester(new FakeSsh(new SshOperationTimeoutException("x")), new ReachableTcp()), (_, _) => throw new InvalidOperationException("boom"));
        var r = await t.TestAsync(Server());
        StringAssert.Contains(r.Items.First(i => i.Status == CheckStatus.Fail).Message, "timed out");
    }
}
