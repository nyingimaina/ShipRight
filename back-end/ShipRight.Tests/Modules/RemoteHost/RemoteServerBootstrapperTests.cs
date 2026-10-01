using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Projects;
using ShipRight.Modules.RemoteHost;
using ShipRight.Shared.SshRunner;

namespace ShipRight.Tests.Modules.RemoteHost;

[TestClass]
public class RemoteServerBootstrapperTests
{
    private sealed class FakeSsh : ISshRunner
    {
        public List<string> Commands { get; } = [];
        public Func<string, int> ExitFor { get; set; } = _ => 0;
        public Func<string, string?> OutputFor { get; set; } = _ => null;

        public async Task<int> RunAsync(string host, string username, string keyPath, string command,
            Func<string, Task>? onOutput = null, Func<string, Task>? onStderr = null, CancellationToken ct = default)
        {
            Commands.Add(command);
            var output = OutputFor(command);
            if (output is not null && onOutput is not null) await onOutput(output);
            return ExitFor(command);
        }
    }

    private static ServerConfig Server() => new()
    {
        Id = "s1", Host = "1.2.3.4", Username = "ec2-user", SshKeyPath = "/k",
        RemoteWorkingDir = "/home/ec2-user/ship-right",
        ComposeRepoUrl = "https://github.com/nyingimaina/ShipRight.git",
        EcrRegion = "us-east-2",
        EcrRegistry = "080632633137.dkr.ecr.us-east-2.amazonaws.com",
        EnvFile = "FOO=bar\nSECRET=p@ss'word\"$x",
    };

    // ── Command builders ─────────────────────────────────────────────────────

    [TestMethod]
    public void BuildInstallDockerCommand_CoversDnfAndApt_AndEnablesService()
    {
        var cmd = RemoteServerBootstrapper.BuildInstallDockerCommand();
        StringAssert.Contains(cmd, "dnf");
        StringAssert.Contains(cmd, "apt-get");
        StringAssert.Contains(cmd, "systemctl enable --now docker");
        StringAssert.Contains(cmd, "usermod -aG docker");
        StringAssert.Contains(cmd, "command -v docker"); // idempotent
    }

    [TestMethod]
    public void BuildInstallDockerCommand_RequiresPasswordlessSudo()
    {
        StringAssert.Contains(RemoteServerBootstrapper.BuildInstallDockerCommand(), "sudo -n");
    }

    [TestMethod]
    public void BuildEnsureComposeCommand_ChecksPlugin()
    {
        StringAssert.Contains(RemoteServerBootstrapper.BuildEnsureComposeCommand(), "docker compose version");
    }

    [TestMethod]
    public void BuildInstallAwsCliCommand_IsIdempotentAndUsesOfficialInstaller()
    {
        var cmd = RemoteServerBootstrapper.BuildInstallAwsCliCommand();
        StringAssert.Contains(cmd, "command -v aws");
        StringAssert.Contains(cmd, "awscli.amazonaws.com");
    }

    [TestMethod]
    public void BuildCloneOrPullCommand_ClonesWhenNoGitDir_ElsePulls()
    {
        var cmd = RemoteServerBootstrapper.BuildCloneOrPullCommand(Server());
        StringAssert.Contains(cmd, "mkdir -p");
        StringAssert.Contains(cmd, "/home/ec2-user/ship-right");
        StringAssert.Contains(cmd, ".git");
        StringAssert.Contains(cmd, "git clone");
        StringAssert.Contains(cmd, "https://github.com/nyingimaina/ShipRight.git");
        StringAssert.Contains(cmd, "git pull");
    }

    [TestMethod]
    public void BuildEcrLoginCommand_PipesTokenToDockerLogin()
    {
        var cmd = RemoteServerBootstrapper.BuildEcrLoginCommand(Server());
        StringAssert.Contains(cmd, "aws ecr get-login-password --region us-east-2");
        StringAssert.Contains(cmd, "docker login --username AWS --password-stdin 080632633137.dkr.ecr.us-east-2.amazonaws.com");
    }

    [TestMethod]
    public void BuildEcrLoginCommand_ReturnsNull_WhenRegistryBlank()
    {
        var s = Server() with { EcrRegistry = string.Empty };
        Assert.IsNull(RemoteServerBootstrapper.BuildEcrLoginCommand(s));
    }

    // ── Orchestration ────────────────────────────────────────────────────────

    [TestMethod]
    public async Task RunAsync_RunsStepsInOrder()
    {
        var ssh = new FakeSsh();
        var result = await new RemoteServerBootstrapper(ssh).RunAsync(Server(), _ => Task.CompletedTask);

        Assert.IsTrue(result.Success);
        Assert.AreEqual(6, ssh.Commands.Count);
        StringAssert.Contains(ssh.Commands[0], "dnf");
        StringAssert.Contains(ssh.Commands[1], "docker compose version");
        StringAssert.Contains(ssh.Commands[2], "command -v aws");
        StringAssert.Contains(ssh.Commands[3], "git clone");
        StringAssert.Contains(ssh.Commands[4], "base64 -d");   // env file
        StringAssert.Contains(ssh.Commands[5], "docker login");
    }

    [TestMethod]
    public async Task RunAsync_StopsAtFirstFailure_AndNamesStep()
    {
        var ssh = new FakeSsh { ExitFor = c => c.Contains("git clone") ? 128 : 0 };
        var result = await new RemoteServerBootstrapper(ssh).RunAsync(Server(), _ => Task.CompletedTask);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.FailedStep!, "clone");
        Assert.AreEqual(4, ssh.Commands.Count);
    }

    [TestMethod]
    public async Task RunAsync_ReportsProgressLines()
    {
        var lines = new List<string>();
        await new RemoteServerBootstrapper(new FakeSsh()).RunAsync(Server(), l => { lines.Add(l); return Task.CompletedTask; });
        Assert.IsTrue(lines.Any(l => l.Contains("Install Docker")));
    }

    [TestMethod]
    public async Task RunAsync_SkipsEnvFileAndEcr_WhenNotConfigured()
    {
        var s = Server() with { EnvFile = string.Empty, EcrRegistry = string.Empty };
        var ssh = new FakeSsh();
        var result = await new RemoteServerBootstrapper(ssh).RunAsync(s, _ => Task.CompletedTask);

        Assert.IsTrue(result.Success);
        Assert.AreEqual(4, ssh.Commands.Count);
    }

    [TestMethod]
    public async Task RunAsync_FailsFast_WhenComposeRepoMissing()
    {
        var s = Server() with { ComposeRepoUrl = string.Empty };
        var ssh = new FakeSsh();
        var result = await new RemoteServerBootstrapper(ssh).RunAsync(s, _ => Task.CompletedTask);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Message, "Compose repo URL");
        Assert.AreEqual(0, ssh.Commands.Count);
    }

    private sealed class ThrowingSsh : ISshRunner
    {
        public Task<int> RunAsync(string host, string username, string keyPath, string command,
            Func<string, Task>? onOutput = null, Func<string, Task>? onStderr = null, CancellationToken ct = default)
            => throw new InvalidOperationException("auth failed");
    }

    [TestMethod]
    public async Task RunAsync_ReturnsFailure_WhenSshThrows()
    {
        var result = await new RemoteServerBootstrapper(new ThrowingSsh()).RunAsync(Server(), _ => Task.CompletedTask);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Message, "auth failed");
        StringAssert.Contains(result.FailedStep!, "Install Docker");
    }

    [TestMethod]
    public async Task RunAsync_NeverEmitsEnvSecretToProgressLines()
    {
        var lines = new List<string>();
        await new RemoteServerBootstrapper(new FakeSsh()).RunAsync(Server(), l => { lines.Add(l); return Task.CompletedTask; });
        Assert.IsFalse(lines.Any(l => l.Contains("SECRET") || l.Contains("p@ss")));
    }

    // ── Env file writer ──────────────────────────────────────────────────────

    [TestMethod]
    public void BuildWriteEnvFileCommand_Base64EncodesContent_AndLocksPermissions()
    {
        var cmd = RemoteEnvFileWriter.BuildWriteEnvFileCommand("/srv/app", "A=1\nB=it's");
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("A=1\nB=it's\n"));
        StringAssert.Contains(cmd, b64);
        StringAssert.Contains(cmd, "base64 -d");
        StringAssert.Contains(cmd, "/srv/app/.env");
        StringAssert.Contains(cmd, "chmod 600");
    }

    [TestMethod]
    public void BuildWriteEnvFileCommand_NeverContainsRawSecret()
    {
        var cmd = RemoteEnvFileWriter.BuildWriteEnvFileCommand("/srv/app", "SECRET=hunter2");
        Assert.IsFalse(cmd.Contains("hunter2"));
    }

    [TestMethod]
    public void BuildWriteEnvFileCommand_NormalisesCrLf()
    {
        var cmd = RemoteEnvFileWriter.BuildWriteEnvFileCommand("/srv/app", "A=1\r\nB=2");
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("A=1\nB=2\n"));
        StringAssert.Contains(cmd, b64);
    }
}
