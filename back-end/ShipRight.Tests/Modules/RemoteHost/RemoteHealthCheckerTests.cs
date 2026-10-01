using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Projects;
using ShipRight.Modules.RemoteHost;
using ShipRight.Shared.SshRunner;

namespace ShipRight.Tests.Modules.RemoteHost;

[TestClass]
public class RemoteHealthCheckerTests
{
    private sealed class FakeSsh : ISshRunner
    {
        public List<string> Commands { get; } = [];
        public Func<string, int, int> ExitFor { get; set; } = (_, _) => 0;

        public async Task<int> RunAsync(string host, string username, string keyPath, string command,
            Func<string, Task>? onOutput = null, Func<string, Task>? onStderr = null, CancellationToken ct = default)
        {
            Commands.Add(command);
            if (onOutput is not null) await onOutput($"out:{Commands.Count}");
            return ExitFor(command, Commands.Count);
        }
    }

    private static ServerConfig Server() => new()
    {
        Host = "h", Username = "u", SshKeyPath = "/k", RemoteWorkingDir = "/srv/app",
    };

    private static RemoteHealthChecker Checker(FakeSsh ssh) =>
        new(ssh, delay: (_, _) => Task.CompletedTask);

    [TestMethod]
    public void BuildProbeCommand_UsesCurlFailSilent_WithDefaultUrl()
    {
        var cmd = RemoteHealthChecker.BuildProbeCommand(Server());
        StringAssert.Contains(cmd, "curl -sf");
        StringAssert.Contains(cmd, "http://localhost:5200/api/health");
    }

    [TestMethod]
    public void BuildProbeCommand_UsesConfiguredUrl()
    {
        var s = Server() with { HealthCheckUrl = "http://localhost:8080/up" };
        StringAssert.Contains(RemoteHealthChecker.BuildProbeCommand(s), "http://localhost:8080/up");
    }

    [TestMethod]
    public void BuildDiagnosticsCommand_ShowsPsAndLogTail()
    {
        var cmd = RemoteHealthChecker.BuildDiagnosticsCommand(Server());
        StringAssert.Contains(cmd, "docker compose ps");
        StringAssert.Contains(cmd, "docker compose logs --tail 50");
        StringAssert.Contains(cmd, "/srv/app");
    }

    [TestMethod]
    public async Task CheckAsync_PassesOnFirstSuccess()
    {
        var ssh = new FakeSsh();
        var r = await Checker(ssh).CheckAsync(Server(), _ => Task.CompletedTask);
        Assert.IsTrue(r.Healthy);
        Assert.AreEqual(1, ssh.Commands.Count);
    }

    [TestMethod]
    public async Task CheckAsync_RetriesUntilHealthy()
    {
        var ssh = new FakeSsh { ExitFor = (_, n) => n < 4 ? 22 : 0 };
        var r = await Checker(ssh).CheckAsync(Server(), _ => Task.CompletedTask);
        Assert.IsTrue(r.Healthy);
        Assert.AreEqual(4, ssh.Commands.Count);
    }

    [TestMethod]
    public async Task CheckAsync_FailsAfterMaxAttempts_AndRunsDiagnostics()
    {
        var ssh = new FakeSsh { ExitFor = (c, _) => c.Contains("curl") ? 22 : 0 };
        var lines = new List<string>();
        var r = await new RemoteHealthChecker(ssh, maxAttempts: 3, delay: (_, _) => Task.CompletedTask)
            .CheckAsync(Server(), l => { lines.Add(l); return Task.CompletedTask; });

        Assert.IsFalse(r.Healthy);
        Assert.AreEqual(4, ssh.Commands.Count); // 3 probes + diagnostics
        StringAssert.Contains(ssh.Commands[3], "docker compose logs");
        Assert.IsTrue(lines.Any(l => l.Contains("out:4")));
    }
}
