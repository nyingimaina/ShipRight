using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Projects;
using ShipRight.Modules.RemoteHost;
using ShipRight.Shared.SshRunner;

namespace ShipRight.Tests.Modules.RemoteHost;

[TestClass]
public class RemoteServerDeployerTests
{
    private sealed class FakeSsh : ISshRunner
    {
        public List<string> Commands { get; } = [];
        public Func<string, int> ExitFor { get; set; } = _ => 0;

        public Task<int> RunAsync(string host, string username, string keyPath, string command,
            Func<string, Task>? onOutput = null, Func<string, Task>? onStderr = null, CancellationToken ct = default)
        {
            Commands.Add(command);
            return Task.FromResult(ExitFor(command));
        }
    }

    private static ServerConfig Server() => new()
    {
        Host = "h", Username = "u", SshKeyPath = "/k", RemoteWorkingDir = "/srv/app",
        EcrRegistry = "080632633137.dkr.ecr.us-east-2.amazonaws.com", EcrRegion = "us-east-2",
        EnvFile = "A=1",
    };

    private static RemoteServerDeployer Deployer(FakeSsh ssh) =>
        new(ssh, new RemoteHealthChecker(ssh, maxAttempts: 2, delay: (_, _) => Task.CompletedTask));

    [TestMethod]
    public void BuildDeployCommand_PullsAndRestartsStack()
    {
        var cmd = RemoteServerDeployer.BuildDeployCommand(Server());
        StringAssert.Contains(cmd, "cd '/srv/app'");
        StringAssert.Contains(cmd, "git pull --ff-only");
        StringAssert.Contains(cmd, "docker compose pull");
        StringAssert.Contains(cmd, "docker compose up -d --remove-orphans");
    }

    [TestMethod]
    public async Task DeployAsync_RunsEnvEcrDeployThenHealth_InOrder()
    {
        var ssh = new FakeSsh();
        var r = await Deployer(ssh).DeployAsync(Server(), _ => Task.CompletedTask);

        Assert.IsTrue(r.Success);
        Assert.AreEqual(4, ssh.Commands.Count);
        StringAssert.Contains(ssh.Commands[0], "base64 -d");
        StringAssert.Contains(ssh.Commands[1], "docker login");
        StringAssert.Contains(ssh.Commands[2], "docker compose up -d");
        StringAssert.Contains(ssh.Commands[3], "curl -sf");
    }

    [TestMethod]
    public async Task DeployAsync_SkipsEnvAndEcr_WhenNotConfigured()
    {
        var s = Server() with { EnvFile = "", EcrRegistry = "" };
        var ssh = new FakeSsh();
        var r = await Deployer(ssh).DeployAsync(s, _ => Task.CompletedTask);

        Assert.IsTrue(r.Success);
        Assert.AreEqual(2, ssh.Commands.Count);
    }

    [TestMethod]
    public async Task DeployAsync_StopsWhenEcrLoginFails()
    {
        var ssh = new FakeSsh { ExitFor = c => c.Contains("docker login") ? 1 : 0 };
        var r = await Deployer(ssh).DeployAsync(Server(), _ => Task.CompletedTask);

        Assert.IsFalse(r.Success);
        StringAssert.Contains(r.Message, "ECR login");
        Assert.IsFalse(ssh.Commands.Any(c => c.Contains("docker compose up")));
    }

    [TestMethod]
    public async Task DeployAsync_Fails_WhenComposeUpFails()
    {
        var ssh = new FakeSsh { ExitFor = c => c.Contains("docker compose up") ? 1 : 0 };
        var r = await Deployer(ssh).DeployAsync(Server(), _ => Task.CompletedTask);

        Assert.IsFalse(r.Success);
        StringAssert.Contains(r.Message, "Deploy command failed");
        Assert.IsFalse(ssh.Commands.Any(c => c.Contains("curl -sf")));
    }

    [TestMethod]
    public async Task DeployAsync_SkipsHealthCheck_WhenConfigured_AndSucceeds()
    {
        var s = Server() with { SkipHealthCheck = true };
        var ssh = new FakeSsh();
        var lines = new List<string>();
        var r = await Deployer(ssh).DeployAsync(s, l => { lines.Add(l); return Task.CompletedTask; });

        Assert.IsTrue(r.Success);
        StringAssert.Contains(r.Message, "health check skipped");
        Assert.AreEqual(3, ssh.Commands.Count);
        Assert.IsFalse(ssh.Commands.Any(c => c.Contains("curl")));
        Assert.IsTrue(lines.Any(l => l.Contains("skipped")));
    }

    [TestMethod]
    public async Task DeployAsync_StillFails_WhenComposeUpFails_AndHealthCheckSkipped()
    {
        var s = Server() with { SkipHealthCheck = true };
        var ssh = new FakeSsh { ExitFor = c => c.Contains("docker compose up") ? 1 : 0 };
        var r = await Deployer(ssh).DeployAsync(s, _ => Task.CompletedTask);
        Assert.IsFalse(r.Success);
    }

    [TestMethod]
    public void SkipHealthCheck_DefaultsToFalse_SoExistingServersKeepChecking()
    {
        Assert.IsFalse(new ServerConfig().SkipHealthCheck);
    }

    [TestMethod]
    public async Task DeployAsync_Fails_WhenEnvWriteFails()
    {
        var ssh = new FakeSsh { ExitFor = c => c.Contains("base64 -d") ? 1 : 0 };
        var r = await Deployer(ssh).DeployAsync(Server(), _ => Task.CompletedTask);

        Assert.IsFalse(r.Success);
        StringAssert.Contains(r.Message, ".env");
        Assert.AreEqual(1, ssh.Commands.Count);
    }

    [TestMethod]
    public async Task DeployAsync_Fails_WhenHealthCheckFails()
    {
        var ssh = new FakeSsh { ExitFor = c => c.Contains("curl -sf") ? 22 : 0 };
        var r = await Deployer(ssh).DeployAsync(Server(), _ => Task.CompletedTask);

        Assert.IsFalse(r.Success);
        StringAssert.Contains(r.Message, "Health check failed");
    }
}
