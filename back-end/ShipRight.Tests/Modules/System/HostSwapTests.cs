using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.RemoteHost;
using ShipRight.Modules.System;
using ShipRight.Shared.SshRunner;

namespace ShipRight.Tests.Modules.HostSwap;

[TestClass]
public class HostSwapTests
{
    private sealed class FakeSsh : ISshRunner
    {
        public string? Host { get; private set; }
        public string? Username { get; private set; }
        public string? KeyPath { get; private set; }
        public string? Command { get; private set; }

        public Task<int> RunAsync(string host, string username, string keyPath, string command,
            Func<string, Task>? onOutput = null, Func<string, Task>? onStderr = null,
            CancellationToken ct = default)
        {
            Host = host;
            Username = username;
            KeyPath = keyPath;
            Command = command;
            return Task.FromResult(0);
        }
    }

    private static HostSwapTargetProvider Target(
        Dictionary<string, string?> env,
        Func<string, bool>? fileExists = null) =>
        new(name => env.TryGetValue(name, out var value) ? value : null, fileExists ?? (_ => true));

    [TestMethod]
    public void Target_ReadsTheMetalHostUserAndKeyFromTheEnvironment()
    {
        var target = Target(new()
        {
            ["SHIPRIGHT__HOST_SWAP__HOST"] = "host.docker.internal",
            ["SHIPRIGHT__HOST_SWAP__USERNAME"] = "ubuntu",
            ["SHIPRIGHT__HOST_SWAP__SSH_KEY_PATH"] = "/root/.shipright/ssh/host-swap.pem",
            ["SHIPRIGHT__HOST_SWAP__SIZE_GB"] = "4",
        }).Get();

        Assert.IsTrue(target.IsConfigured);
        Assert.AreEqual("host.docker.internal", target.Host);
        Assert.AreEqual("ubuntu", target.Username);
        Assert.AreEqual("/root/.shipright/ssh/host-swap.pem", target.SshKeyPath);
        Assert.AreEqual(4, target.SizeGb);
    }

    [TestMethod]
    public void Target_IsNotConfiguredUntilEveryPartExists()
    {
        var target = Target(new()
        {
            ["SHIPRIGHT__HOST_SWAP__HOST"] = "host.docker.internal",
            ["SHIPRIGHT__HOST_SWAP__USERNAME"] = "ubuntu",
            ["SHIPRIGHT__HOST_SWAP__SSH_KEY_PATH"] = "/root/.shipright/ssh/host-swap.pem",
        }, _ => false).Get();

        Assert.IsFalse(target.IsConfigured);
    }

    [TestMethod]
    public void Target_DefaultsToTwoGigabytesAndClampsOutOfRangeValues()
    {
        var defaults = Target(new()).Get();
        var oversized = Target(new() { ["SHIPRIGHT__HOST_SWAP__SIZE_GB"] = "99" }).Get();

        Assert.AreEqual(2, defaults.SizeGb);
        Assert.AreEqual(8, oversized.SizeGb);
    }

    [TestMethod]
    public async Task Stats_ReadsSwapTotalsAndReportsTheUsedPercentage()
    {
        var stats = new HostSwapStatsProvider(() => (512L * 1024 * 1024, 2048L * 1024 * 1024));

        var snapshot = await stats.GetAsync();

        Assert.IsTrue(snapshot.Available);
        Assert.AreEqual(512L * 1024 * 1024, snapshot.SwapUsedBytes);
        Assert.AreEqual(2048L * 1024 * 1024, snapshot.SwapTotalBytes);
        Assert.AreEqual(25, snapshot.SwapUsedPercent, 0.001);
    }

    [TestMethod]
    public async Task Stats_WithNoSwapConfigured_IsStillAvailableAndReportsZeroes()
    {
        var stats = new HostSwapStatsProvider(() => (0, 0));

        var snapshot = await stats.GetAsync();

        Assert.IsTrue(snapshot.Available);
        Assert.AreEqual(0, snapshot.SwapTotalBytes);
        Assert.AreEqual(0, snapshot.SwapUsedPercent);
    }

    [TestMethod]
    public async Task Stats_FailingReader_IsReportedWithoutThrowing()
    {
        var stats = new HostSwapStatsProvider(() => throw new IOException("no proc"));

        var snapshot = await stats.GetAsync();

        Assert.IsFalse(snapshot.Available);
        Assert.AreEqual(0, snapshot.SwapTotalBytes);
    }

    [TestMethod]
    public async Task Service_SendsTheSwapCommandToTheConfiguredMetalHost()
    {
        var ssh = new FakeSsh();
        var service = new HostSwapService(Target(new()
        {
            ["SHIPRIGHT__HOST_SWAP__HOST"] = "host.docker.internal",
            ["SHIPRIGHT__HOST_SWAP__USERNAME"] = "ubuntu",
            ["SHIPRIGHT__HOST_SWAP__SSH_KEY_PATH"] = "/root/.shipright/ssh/host-swap.pem",
        }), ssh);

        var result = await service.CreateAsync(2);

        Assert.IsTrue(result.Success);
        Assert.AreEqual("host.docker.internal", ssh.Host);
        Assert.AreEqual("ubuntu", ssh.Username);
        Assert.AreEqual("/root/.shipright/ssh/host-swap.pem", ssh.KeyPath);
        StringAssert.Contains(ssh.Command, "fallocate -l 2G /swapfile");
        StringAssert.Contains(ssh.Command, "swapon /swapfile");
    }

    [TestMethod]
    public async Task Service_RefusesToRunWhenNoMetalHostIsConfigured()
    {
        var service = new HostSwapService(Target(new()), new FakeSsh());

        await Assert.ThrowsExceptionAsync<HostSwapNotConfiguredException>(() => service.CreateAsync(2));
    }
}
