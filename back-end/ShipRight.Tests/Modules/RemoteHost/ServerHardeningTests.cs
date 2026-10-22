using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Projects;
using ShipRight.Modules.RemoteHost;
using ShipRight.Shared.SshRunner;

namespace ShipRight.Tests.Modules.RemoteHost;

/// <summary>ECR-login timeout, predictable git state on the server, and swap creation.</summary>
[TestClass]
public class ServerHardeningTests
{
    private static ServerConfig Server() => new()
    {
        Host = "h", Username = "u", SshKeyPath = "/k", RemoteWorkingDir = "/home/ubuntu/ship-right/",
        ComposeRepoUrl = "https://github.com/x/y.git",
        EcrRegistry = "080632633137.dkr.ecr.us-east-2.amazonaws.com", EcrRegion = "us-east-2",
    };

    private sealed class FakeSsh : ISshRunner
    {
        public List<string> Commands { get; } = [];
        public Func<string, int> ExitFor { get; set; } = _ => 0;
        public Func<string, string?> OutputFor { get; set; } = _ => null;
        public async Task<int> RunAsync(string host, string username, string keyPath, string command,
            Func<string, Task>? onOutput = null, Func<string, Task>? onStderr = null, CancellationToken ct = default)
        {
            Commands.Add(command);
            var o = OutputFor(command);
            if (o is not null && onOutput is not null) await onOutput(o);
            return ExitFor(command);
        }
    }

    // ── ECR login timeout ────────────────────────────────────────────────────

    [TestMethod]
    public void EcrLogin_IsTimeBoxed_AndStillLogsIn()
    {
        var cmd = RemoteEcrLogin.BuildWithTimeout(Server())!;
        StringAssert.Contains(cmd, "timeout 90");
        StringAssert.Contains(cmd, "aws ecr get-login-password --region us-east-2");
        StringAssert.Contains(cmd, "docker login --username AWS --password-stdin 080632633137.dkr.ecr.us-east-2.amazonaws.com");
    }

    [TestMethod]
    public void EcrLogin_ExplainsATimeoutAndKeepsTheExitCode()
    {
        var cmd = RemoteEcrLogin.BuildWithTimeout(Server())!;
        StringAssert.Contains(cmd, "124");
        StringAssert.Contains(cmd, "out of memory");
        StringAssert.Contains(cmd, "exit $rc");
    }

    [TestMethod]
    public void EcrLogin_IsNullWhenNoRegistry() =>
        Assert.IsNull(RemoteEcrLogin.BuildWithTimeout(Server() with { EcrRegistry = "" }));

    [TestMethod]
    public async Task Bootstrap_UsesTheTimeBoxedLogin()
    {
        var ssh = new FakeSsh();
        await new ResilientServerBootstrapper(ssh).RunAsync(Server(), _ => Task.CompletedTask);
        Assert.IsTrue(ssh.Commands.Any(c => c.Contains("docker login") && c.Contains("timeout 90")));
    }

    [TestMethod]
    public async Task Deploy_UsesTheTimeBoxedLogin()
    {
        var ssh = new FakeSsh();
        var s = Server() with { SkipHealthCheck = true };
        await new RemoteServerDeployer(ssh, new RemoteHealthChecker(ssh, 1, (_, _) => Task.CompletedTask))
            .DeployAsync(s, _ => Task.CompletedTask);
        Assert.IsTrue(ssh.Commands.Any(c => c.Contains("docker login") && c.Contains("timeout 90")));
    }

    // ── Predictable git state ────────────────────────────────────────────────

    [TestMethod]
    public void SafePull_IgnoresSecretsAndHomeFolderNoise_WithoutTouchingTheRepo()
    {
        var f = RemoteGitHygiene.BuildSafePullFragment();
        StringAssert.Contains(f, ".git/info/exclude");
        foreach (var rule in new[] { ".env", ".shipright/", ".ssh/", ".aws/", ".docker/", ".cache/", ".bashrc" })
            StringAssert.Contains(f, rule);
    }

    [TestMethod]
    public void SafePull_CommitsLocalChanges_ThenRebasesOntoTheRemote()
    {
        var f = RemoteGitHygiene.BuildSafePullFragment();
        var add = f.IndexOf("git add -A", StringComparison.Ordinal);
        var commit = f.IndexOf("git diff --cached --quiet ||", StringComparison.Ordinal);
        var pull = f.IndexOf("pull --rebase", StringComparison.Ordinal);
        Assert.IsTrue(add >= 0 && commit > add && pull > commit, "add → commit-if-needed → pull");
        StringAssert.Contains(f, "user.name=ShipRight");
    }

    [TestMethod]
    public void SafePull_NeverAutoCommitsWhenTheRepoIsTheHomeFolder()
    {
        var f = RemoteGitHygiene.BuildSafePullFragment();
        StringAssert.Contains(f, "git rev-parse --show-toplevel");
        StringAssert.Contains(f, "\"$HOME\"");
        StringAssert.Contains(f, "git pull --ff-only");   // the home-folder path falls back to the plain pull
    }

    [TestMethod]
    public void SafePull_AbortsAConflictedRebase_SoTheTreeIsNeverLeftHalfMerged()
    {
        var f = RemoteGitHygiene.BuildSafePullFragment();
        StringAssert.Contains(f, "git rebase --abort");
        StringAssert.Contains(f, "exit 1");
    }

    [TestMethod]
    public void SafeSync_KeepsTheIdempotentFirstRunBranch_AndUsesTheSafePull()
    {
        var cmd = RemoteGitHygiene.BuildSafeSyncRepoCommand(Server());
        StringAssert.Contains(cmd, "mkdir -p '/home/ubuntu/ship-right'");
        StringAssert.Contains(cmd, "git init -q");
        StringAssert.Contains(cmd, "git checkout -f -B");
        StringAssert.Contains(cmd, "git add -A");
        Assert.IsFalse(cmd.Contains("git pull --ff-only && "), "the plain pull must not be the only update path");
    }

    [TestMethod]
    public async Task Bootstrap_SyncStepUsesTheSafePull()
    {
        var ssh = new FakeSsh();
        await new ResilientServerBootstrapper(ssh).RunAsync(Server(), _ => Task.CompletedTask);
        Assert.IsTrue(ssh.Commands.Any(c => c.Contains("git init -q") && c.Contains("git add -A")));
    }

    [TestMethod]
    public void DeployCommand_UsesTheSafePull()
    {
        var cmd = RemoteServerDeployer.BuildVerboseDeployCommand(Server());
        StringAssert.Contains(cmd, "git add -A");
        StringAssert.Contains(cmd, "pull --rebase");
        StringAssert.Contains(cmd, "docker compose pull");
    }

    // ── Swap ─────────────────────────────────────────────────────────────────

    [TestMethod]
    public void Swap_CreateCommand_IsIdempotentAndPersistent()
    {
        var cmd = RemoteSwap.BuildCreateCommand(2);
        StringAssert.Contains(cmd, "swapon --show");               // already has swap → nothing to do
        StringAssert.Contains(cmd, "fallocate -l 2G /swapfile");
        StringAssert.Contains(cmd, "chmod 600 /swapfile");
        StringAssert.Contains(cmd, "mkswap /swapfile");
        StringAssert.Contains(cmd, "swapon /swapfile");
        StringAssert.Contains(cmd, "/etc/fstab");
        StringAssert.Contains(cmd, "free -m");
    }

    [TestMethod]
    public void Swap_CreateCommand_NeverBlocksOnAPasswordPrompt_AndChecksDiskSpaceFirst()
    {
        var cmd = RemoteSwap.BuildCreateCommand(2);
        StringAssert.Contains(cmd, "sudo -n");
        StringAssert.Contains(cmd, "df ");
        StringAssert.Contains(cmd, "Not enough free disk space");
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    [DataRow(9)]
    public void Swap_RejectsSillySizes(int gb) =>
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => RemoteSwap.BuildCreateCommand(gb));

    [TestMethod]
    public async Task Swap_RunAsync_ReportsTheServersOutput()
    {
        var ssh = new FakeSsh { OutputFor = _ => "Swap: 2047 0 2047" };
        var r = await new RemoteSwap(ssh).CreateAsync(Server(), 2);
        Assert.IsTrue(r.Success);
        StringAssert.Contains(r.Output, "Swap: 2047");
        Assert.AreEqual(1, ssh.Commands.Count);
    }

    [TestMethod]
    public async Task Swap_RunAsync_FailureCarriesTheLastLine()
    {
        var ssh = new FakeSsh { ExitFor = _ => 1, OutputFor = _ => "Not enough free disk space for a 2G swap file." };
        var r = await new RemoteSwap(ssh).CreateAsync(Server(), 2);
        Assert.IsFalse(r.Success);
        StringAssert.Contains(r.Output, "Not enough free disk space");
    }
}
