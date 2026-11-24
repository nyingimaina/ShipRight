using Microsoft.VisualStudio.TestTools.UnitTesting;
using Serilog;
using ShipRight.Modules.ConnectionTest;
using ShipRight.Modules.Projects;
using ShipRight.Modules.RemoteHost;
using ShipRight.Shared.SshRunner;

namespace ShipRight.Tests.Modules.ServerNetwork;

/// <summary>
/// These tests pin the direct-network behaviour that the server-network provider refactor must preserve.
/// They deliberately observe the public seam: every remote operation must still hand the stored server's
/// host, username and key path to the SSH runner. They must pass before any provider/route code is added.
/// </summary>
[TestClass]
public class ServerNetworkCharacterizationTests
{
    private sealed record SshCall(string Host, string Username, string KeyPath, string Command);

    private sealed class CapturingSsh : ISshRunner
    {
        public List<SshCall> Calls { get; } = [];
        public Func<SshCall, int> ExitFor { get; set; } = _ => 0;
        public Func<SshCall, IReadOnlyList<string>> OutputFor { get; set; } = _ => [];

        public async Task<int> RunAsync(string host, string username, string keyPath, string command,
            Func<string, Task>? onOutput = null, Func<string, Task>? onStderr = null,
            CancellationToken ct = default)
        {
            var call = new SshCall(host, username, keyPath, command);
            Calls.Add(call);
            ct.ThrowIfCancellationRequested();

            var output = OutputFor(call);
            if (onOutput is not null)
            {
                foreach (var line in output) await onOutput(line);
            }
            else if (onStderr is not null)
            {
                foreach (var line in output) await onStderr(line);
            }

            return ExitFor(call);
        }
    }

    private sealed class CapturingTcp : ITcpProbe
    {
        public List<(string Host, int Port)> Calls { get; } = [];
        public TcpProbeResult Result { get; set; } = TcpProbeResult.Success;

        public Task<TcpProbeResult> ProbeAsync(string host, int port, TimeSpan timeout, CancellationToken ct)
        {
            Calls.Add((host, port));
            return Task.FromResult(Result);
        }
    }

    private static ServerConfig Server() => new()
    {
        Id = "server-1",
        Name = "prod",
        Host = "box.tail1.ts.net",
        Username = "ubuntu",
        SshKeyPath = "/keys/server.pem",
        RemoteWorkingDir = "/srv/app",
    };

    private static void AssertUsedSavedEndpoint(ServerConfig server, CapturingSsh ssh)
    {
        Assert.IsTrue(ssh.Calls.Count > 0, "the operation must perform at least one SSH call");
        foreach (var call in ssh.Calls)
        {
            Assert.AreEqual(server.Host, call.Host);
            Assert.AreEqual(server.Username, call.Username);
            Assert.AreEqual(server.SshKeyPath, call.KeyPath);
            Assert.IsFalse(string.IsNullOrWhiteSpace(call.Command), "every SSH call must carry a command");
        }
    }

    [TestMethod]
    public async Task ConnectionTest_UsesTheSavedHostOnPort22AndTheSavedKey()
    {
        var server = Server();
        var ssh = new CapturingSsh
        {
            OutputFor = _ => ["user:ubuntu", "sudo:ok", "git:ok", "docker:installed", "dockeruse:ok", "compose:ok", "disk:50000", "dir:ok", "pkg:apt"],
        };
        var tcp = new CapturingTcp();

        var result = await new ConnectionTester(ssh, tcp).TestAsync(server);

        Assert.IsTrue(result.Ready);
        Assert.AreEqual((server.Host, 22), tcp.Calls.Single());
        AssertUsedSavedEndpoint(server, ssh);
    }

    [TestMethod]
    public async Task RemoteSwap_UsesTheSavedEndpointAndReturnsTheCombinedOutput()
    {
        var server = Server();
        var ssh = new CapturingSsh { OutputFor = _ => ["Swap enabled.", "free output"] };

        var result = await new RemoteSwap(ssh).CreateAsync(server, 2);

        Assert.IsTrue(result.Success);
        StringAssert.Contains(result.Output, "Swap enabled.");
        StringAssert.Contains(result.Output, "free output");
        AssertUsedSavedEndpoint(server, ssh);
        StringAssert.Contains(ssh.Calls.Single().Command, "fallocate -l 2G /swapfile");
    }

    [TestMethod]
    public async Task RemoteHealthChecker_UsesTheSavedEndpointForProbeAndDiagnostics()
    {
        var server = Server();
        var ssh = new CapturingSsh
        {
            ExitFor = call => call.Command.StartsWith("curl", StringComparison.Ordinal) ? 22 : 0,
        };
        var checker = new RemoteHealthChecker(ssh, maxAttempts: 1, delay: (_, _) => Task.CompletedTask);

        var result = await checker.CheckAsync(server, _ => Task.CompletedTask);

        Assert.IsFalse(result.Healthy);
        Assert.AreEqual(2, ssh.Calls.Count);
        AssertUsedSavedEndpoint(server, ssh);
        StringAssert.StartsWith(ssh.Calls[0].Command, "curl -sf");
        StringAssert.StartsWith(ssh.Calls[1].Command, "cd '/srv/app'");
    }

    [TestMethod]
    public async Task RemoteServerDeployer_RunsEveryStageAgainstTheSavedEndpoint()
    {
        var server = Server() with
        {
            EnvFile = "A=1",
            EcrRegistry = "080632633137.dkr.ecr.us-east-2.amazonaws.com",
            EcrRegion = "us-east-2",
            SkipHealthCheck = true,
        };
        var ssh = new CapturingSsh();
        var checker = new RemoteHealthChecker(ssh, maxAttempts: 1, delay: (_, _) => Task.CompletedTask);

        var result = await new RemoteServerDeployer(ssh, checker).DeployAsync(server, _ => Task.CompletedTask);

        Assert.IsTrue(result.Success);
        Assert.AreEqual(3, ssh.Calls.Count);
        AssertUsedSavedEndpoint(server, ssh);
    }

    [TestMethod]
    public async Task RemoteServerBootstrapper_UsesTheSavedEndpointForEveryStep()
    {
        var server = Server() with
        {
            ComposeRepoUrl = "git@github.com:example/stack.git",
            EnvFile = "A=1",
            EcrRegistry = "080632633137.dkr.ecr.us-east-2.amazonaws.com",
            EcrRegion = "us-east-2",
        };
        var ssh = new CapturingSsh();

        var result = await new RemoteServerBootstrapper(ssh).RunAsync(server, _ => Task.CompletedTask);

        Assert.IsTrue(result.Success);
        Assert.AreEqual(6, ssh.Calls.Count);
        AssertUsedSavedEndpoint(server, ssh);
    }

    [TestMethod]
    public async Task RemoteCheckpointLogReader_UsesTheSavedEndpoint()
    {
        var server = Server();
        var ssh = new CapturingSsh { OutputFor = _ => ["checkpoint 1"] };

        var result = await new RemoteCheckpointLogReader(ssh).ReadAsync(server);

        Assert.IsTrue(result.Success);
        StringAssert.Contains(result.Content, "checkpoint 1");
        AssertUsedSavedEndpoint(server, ssh);
        StringAssert.Contains(ssh.Calls.Single().Command, "checkpoints.log");
    }

    [TestMethod]
    public async Task LinuxSshMonitoringProvider_UsesTheConfiguredEndpointAndKeyPath()
    {
        var ssh = new CapturingSsh();
        var provider = new LinuxSshMonitoringProvider(ssh);
        var config = new RemoteHostConfig("100.64.0.10", 22, "ec2-user");

        var metrics = await provider.GetMetricsAsync(config, "/keys/monitor.pem");

        Assert.IsTrue(metrics.Reachable);
        var call = ssh.Calls.Single();
        Assert.AreEqual("100.64.0.10", call.Host);
        Assert.AreEqual("ec2-user", call.Username);
        Assert.AreEqual("/keys/monitor.pem", call.KeyPath);
        StringAssert.Contains(call.Command, "===CPU1===");
    }

    [TestMethod]
    public async Task DiagnosticSshRunner_ForwardsTheEndpointArgumentsUnchanged()
    {
        var inner = new CapturingSsh();
        var wrapper = new DiagnosticSshRunner(inner, new LoggerConfiguration().CreateLogger());

        await wrapper.RunAsync("box.tail1.ts.net", "ubuntu", "/keys/server.pem", "uptime");

        var call = inner.Calls.Single();
        Assert.AreEqual("box.tail1.ts.net", call.Host);
        Assert.AreEqual("ubuntu", call.Username);
        Assert.AreEqual("/keys/server.pem", call.KeyPath);
        Assert.AreEqual("uptime", call.Command);
    }
}
