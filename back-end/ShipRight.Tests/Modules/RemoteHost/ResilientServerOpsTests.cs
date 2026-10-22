using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Projects;
using ShipRight.Modules.RemoteHost;
using ShipRight.Shared.SshRunner;

namespace ShipRight.Tests.Modules.RemoteHost;

[TestClass]
public class ResilientServerOpsTests
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
            if (output is not null && onOutput is not null)
                foreach (var l in output.Split('\n')) await onOutput(l);
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
        EnvFile = "FOO=bar",
    };

    // ── Idempotent repo sync ─────────────────────────────────────────────────

    [TestMethod]
    public void BuildSyncRepoCommand_HandlesNonEmptyDirWithoutGit()
    {
        var cmd = RemoteCheckpoint.BuildSyncRepoCommand(Server());
        StringAssert.Contains(cmd, "git init");
        StringAssert.Contains(cmd, "git fetch");
        StringAssert.Contains(cmd, "git checkout");
        StringAssert.Contains(cmd, "git pull --ff-only");
        StringAssert.Contains(cmd, "https://github.com/nyingimaina/ShipRight.git");
        Assert.IsFalse(cmd.Contains("git clone"), "clone into a non-empty dir fails with exit 128");
    }

    // ── Checkpoint wrapper ───────────────────────────────────────────────────

    [TestMethod]
    public void Wrap_RecordsStartOkAndFail_AndPreservesExitCode()
    {
        var cmd = RemoteCheckpoint.Wrap("bootstrap", "Install Docker", "echo hi");
        StringAssert.Contains(cmd, ".shipright/checkpoints.log");
        StringAssert.Contains(cmd, "START");
        StringAssert.Contains(cmd, "OK");
        StringAssert.Contains(cmd, "FAIL");
        StringAssert.Contains(cmd, "echo hi");
        StringAssert.Contains(cmd, "exit $rc");
    }

    [TestMethod]
    public void Wrap_NeverBreaksOnQuotesInStageName()
    {
        var cmd = RemoteCheckpoint.Wrap("bootstrap", "it's \"odd\"", "true");
        Assert.IsFalse(cmd.Contains("it's"));
    }

    [TestMethod]
    public void BuildDiagnosticsCommand_ListsDirGitStateAndCheckpoints()
    {
        var cmd = RemoteCheckpoint.BuildDiagnosticsCommand(Server());
        StringAssert.Contains(cmd, "ls -la '/home/ec2-user/ship-right'");
        StringAssert.Contains(cmd, "git -C '/home/ec2-user/ship-right' status");
        StringAssert.Contains(cmd, "checkpoints.log");
    }

    // ── Bootstrapper ─────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Bootstrap_RunsSixWrappedSteps_AndUsesSyncNotClone()
    {
        var ssh = new FakeSsh();
        var result = await new ResilientServerBootstrapper(ssh).RunAsync(Server(), _ => Task.CompletedTask);

        Assert.IsTrue(result.Success);
        Assert.AreEqual(8, ssh.Commands.Count); // 4 setup + .env + domain check + settings check + ECR login
        Assert.IsTrue(ssh.Commands.All(c => c.Contains("checkpoints.log")));
        Assert.IsFalse(ssh.Commands.Any(c => c.Contains("git clone")));
        StringAssert.Contains(ssh.Commands[3], "git init");
    }

    [TestMethod]
    public async Task Bootstrap_OnFailure_RunsDiagnostics_AndEmitsOutputToStream()
    {
        var ssh = new FakeSsh
        {
            ExitFor = c => c.Contains("git init") ? 128 : 0,
            OutputFor = c => c.Contains("git init") ? "fatal: boom"
                : c.Contains("--- diagnostics ---") ? "drwxr-xr-x 2 ec2-user x"
                : null,
        };
        var lines = new List<string>();
        var result = await new ResilientServerBootstrapper(ssh)
            .RunAsync(Server(), l => { lines.Add(l); return Task.CompletedTask; });

        Assert.IsFalse(result.Success);
        Assert.AreEqual("Compose repo sync", result.FailedStep);
        StringAssert.Contains(result.Message, "fatal: boom");
        Assert.AreEqual(5, ssh.Commands.Count); // 4 steps + diagnostics
        StringAssert.Contains(ssh.Commands[4], "--- diagnostics ---");
        Assert.IsTrue(lines.Any(l => l.Contains("drwxr-xr-x")));
        Assert.IsTrue(lines.Any(l => l.Contains("checkpoints.log")));
    }

    [TestMethod]
    public async Task Bootstrap_DiagnosticsFailureDoesNotMaskOriginalError()
    {
        var ssh = new FakeSsh
        {
            ExitFor = c => c.Contains("git init") || c.Contains("--- diagnostics ---") ? 1 : 0,
        };
        var result = await new ResilientServerBootstrapper(ssh).RunAsync(Server(), _ => Task.CompletedTask);

        Assert.IsFalse(result.Success);
        Assert.AreEqual("Compose repo sync", result.FailedStep);
    }

    [TestMethod]
    public async Task Bootstrap_FailsFast_WhenComposeRepoMissing()
    {
        var ssh = new FakeSsh();
        var result = await new ResilientServerBootstrapper(ssh)
            .RunAsync(Server() with { ComposeRepoUrl = "" }, _ => Task.CompletedTask);

        Assert.IsFalse(result.Success);
        Assert.AreEqual(0, ssh.Commands.Count);
    }

    [TestMethod]
    public async Task Bootstrap_NeverEmitsEnvSecret()
    {
        var lines = new List<string>();
        var s = Server() with { EnvFile = "SECRET=hunter2" };
        await new ResilientServerBootstrapper(new FakeSsh()).RunAsync(s, l => { lines.Add(l); return Task.CompletedTask; });
        Assert.IsFalse(lines.Any(l => l.Contains("hunter2")));
    }

    // ── Deployer ─────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Deploy_WrapsStagesWithCheckpoints()
    {
        var ssh = new FakeSsh();
        var s = Server() with { SkipHealthCheck = true };
        var result = await new ResilientServerDeployer(ssh).DeployAsync(s, _ => Task.CompletedTask);

        Assert.IsTrue(result.Success);
        Assert.AreEqual(3, ssh.Commands.Count);
        Assert.IsTrue(ssh.Commands.All(c => c.Contains("checkpoints.log")));
        StringAssert.Contains(ssh.Commands[2], "docker compose up");
    }

    [TestMethod]
    public void BuildDiagnosticsCommand_IncludesDockerComposeState()
    {
        var cmd = RemoteCheckpoint.BuildDiagnosticsCommand(Server());
        StringAssert.Contains(cmd, "docker compose config");
        StringAssert.Contains(cmd, "docker compose ps");
        StringAssert.Contains(cmd, "docker compose logs");
        StringAssert.Contains(cmd, "cd '/home/ec2-user/ship-right'");
    }

    [TestMethod]
    public async Task Deploy_OnFailure_MessageCarriesLastOutputLine()
    {
        var ssh = new FakeSsh
        {
            ExitFor = c => c.Contains("docker compose up") ? 1 : 0,
            OutputFor = c => c.Contains("docker compose up") ? "pull access denied for foo" : null,
        };
        var s = Server() with { SkipHealthCheck = true };
        var result = await new ResilientServerDeployer(ssh).DeployAsync(s, _ => Task.CompletedTask);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Message, "pull access denied for foo");
    }

    private const string BuildNoDockerfileOutput =
        " app Skipped - No image to be pulled \n" +
        "failed to solve: failed to read dockerfile: open Dockerfile: no such file or directory\n" +
        "#1 DONE 0.0s";

    [TestMethod]
    public async Task Deploy_OnFailure_MessageNamesTheRealErrorNotTrailingNoise()
    {
        var ssh = new FakeSsh
        {
            ExitFor = c => c.Contains("docker compose up") ? 1 : 0,
            OutputFor = c => c.Contains("docker compose up") ? BuildNoDockerfileOutput : null,
        };
        var result = await new ResilientServerDeployer(ssh)
            .DeployAsync(Server() with { SkipHealthCheck = true }, _ => Task.CompletedTask);

        StringAssert.Contains(result.Message, "failed to read dockerfile");
        Assert.IsFalse(result.Message.Contains("#1 DONE"));
    }

    [TestMethod]
    public async Task Deploy_ServiceBuildsInsteadOfPullingAnImage_ExplainsHowToFix()
    {
        var ssh = new FakeSsh
        {
            ExitFor = c => c.Contains("docker compose up") ? 1 : 0,
            OutputFor = c => c.Contains("docker compose up") ? BuildNoDockerfileOutput : null,
        };
        var lines = new List<string>();
        var result = await new ResilientServerDeployer(ssh)
            .DeployAsync(Server() with { SkipHealthCheck = true }, l => { lines.Add(l); return Task.CompletedTask; });

        StringAssert.Contains(result.Message, "image:");
        StringAssert.Contains(result.Message, "build:");
        Assert.IsTrue(lines.Any(l => l.StartsWith("[hint]") && l.Contains("image:")));
    }

    [TestMethod]
    public async Task Deploy_UnrelatedFailure_HasNoBuildHint()
    {
        var ssh = new FakeSsh
        {
            ExitFor = c => c.Contains("docker compose up") ? 1 : 0,
            OutputFor = c => c.Contains("docker compose up") ? "pull access denied for foo" : null,
        };
        var result = await new ResilientServerDeployer(ssh)
            .DeployAsync(Server() with { SkipHealthCheck = true }, _ => Task.CompletedTask);
        Assert.IsFalse(result.Message.Contains("build:"));
    }

    [TestMethod]
    public async Task Deploy_OnFailure_RunsDiagnostics()
    {
        var ssh = new FakeSsh { ExitFor = c => c.Contains("docker compose up") ? 1 : 0 };
        var s = Server() with { SkipHealthCheck = true };
        var lines = new List<string>();
        var result = await new ResilientServerDeployer(ssh).DeployAsync(s, l => { lines.Add(l); return Task.CompletedTask; });

        Assert.IsFalse(result.Success);
        Assert.IsTrue(ssh.Commands.Last().Contains("--- diagnostics ---"));
    }
}
