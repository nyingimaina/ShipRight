using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Projects;
using ShipRight.Modules.RemoteHost;
using ShipRight.Shared.SshRunner;

namespace ShipRight.Tests.Modules.RemoteHost;

/// <summary>
/// A deploy must leave the server ready for the new install without losing data: leftover containers from an
/// earlier install are replaced (their data volumes are kept and adopted), and unused images are pruned.
/// </summary>
[TestClass]
public class DeployCleanSlateTests
{
    private static ServerConfig Server() => new()
    {
        Host = "h", Username = "u", SshKeyPath = "/k", RemoteWorkingDir = "/home/ubuntu/ship-right",
        EcrRegistry = "080632633137.dkr.ecr.us-east-2.amazonaws.com", EnvFile = "A=1", SkipHealthCheck = true,
    };

    // ── leftover containers ──────────────────────────────────────────────────

    [TestMethod]
    public void CleanSlate_ReplacesLeftoverContainersFromAnotherComposeProject()
    {
        var s = RemoteDeployPrep.BuildCleanSlateFragment();
        foreach (var name in new[] { "shipright-db", "shipright-app", "shipright-caddy" })
            StringAssert.Contains(s, name);
        StringAssert.Contains(s, "com.docker.compose.project");
        StringAssert.Contains(s, "docker stop -t 60");
        StringAssert.Contains(s, "docker rm ");
    }

    [TestMethod]
    public void CleanSlate_NeverDeletesDataVolumes()
    {
        var s = RemoteDeployPrep.BuildCleanSlateFragment();
        Assert.IsFalse(System.Text.RegularExpressions.Regex.IsMatch(s, @"docker (container )?rm\s+-[a-zA-Z]*v"),
            "container removal must not take volumes with it");
        Assert.IsFalse(s.Contains("docker volume rm shipright-db-data;"), "adoption must only roll back a half-made copy");
        Assert.IsFalse(s.Contains("--volumes"));
        Assert.IsFalse(s.Contains("down -v"));
    }

    [TestMethod]
    public void CleanSlate_StopsTheOldDatabaseBeforeCopyingItsData()
    {
        var s = RemoteDeployPrep.BuildCleanSlateFragment();
        Assert.IsTrue(s.IndexOf("docker stop -t 60", StringComparison.Ordinal) < s.IndexOf("cp -a", StringComparison.Ordinal),
            "a database volume is only copied after its container has shut down cleanly");
    }

    // ── adopting data from an earlier install ────────────────────────────────

    [TestMethod]
    public void CleanSlate_AdoptsDataFromAnEarlierProjectsVolumes()
    {
        var s = RemoteDeployPrep.BuildCleanSlateFragment();
        foreach (var v in new[] { "shipright-db-data", "shipright-app-data", "shipright-caddy-data", "shipright-caddy-config" })
            StringAssert.Contains(s, v);
        StringAssert.Contains(s, "docker volume inspect");                 // already adopted/created → skip
        StringAssert.Contains(s, "docker volume ls -q");
        StringAssert.Contains(s, "du -sk");                                // pick the volume that actually holds data
        StringAssert.Contains(s, "cp -a /from/. /to/");
        StringAssert.Contains(s, "The old volume is kept");
    }

    // ── disk space ───────────────────────────────────────────────────────────

    [TestMethod]
    public void DiskGuard_PrunesWhenLow_AndRefusesWhenCritical()
    {
        var s = RemoteDeployPrep.BuildDiskGuardFragment();
        StringAssert.Contains(s, "df -Pm /");
        StringAssert.Contains(s, "docker image prune -af");
        StringAssert.Contains(s, "Not enough disk space");
        StringAssert.Contains(s, "exit 1");
    }

    [TestMethod]
    public void Prune_RemovesOldUnusedImagesButNeverVolumes_AndNeverFailsTheDeploy()
    {
        var s = RemoteDeployPrep.BuildPruneFragment();
        StringAssert.Contains(s, "docker image prune -f");
        StringAssert.Contains(s, "docker image prune -af --filter 'until=168h'");
        StringAssert.Contains(s, "docker builder prune");
        Assert.IsFalse(s.Contains("volume prune") || s.Contains("system prune") || s.Contains("--volumes"),
            "data volumes and other apps' containers must never be pruned");
        StringAssert.EndsWith(s.TrimEnd(), "true");
        StringAssert.Contains(s, "df -h /");
    }

    // ── wired into the deploy ────────────────────────────────────────────────

    [TestMethod]
    public void DeployCommand_RunsPrepAfterTheGitPull_AndPrunesOnlyAfterASuccessfulStart()
    {
        var cmd = RemoteServerDeployer.BuildVerboseDeployCommand(Server());
        var pull = cmd.IndexOf("pull --rebase", StringComparison.Ordinal);
        var clean = cmd.IndexOf("com.docker.compose.project", StringComparison.Ordinal);
        var guard = cmd.IndexOf("Not enough disk space", StringComparison.Ordinal);
        var composePull = cmd.IndexOf("docker compose pull", StringComparison.Ordinal);
        var up = cmd.IndexOf("docker compose up -d", StringComparison.Ordinal);
        var prune = cmd.IndexOf("docker image prune -f", StringComparison.Ordinal);
        Assert.IsTrue(pull < clean && clean < guard && guard < composePull && composePull < up && up < prune,
            "git pull → clean slate → disk guard → pull images → up → prune");
        StringAssert.Contains(cmd, "if [ $rc -eq 0 ]");
        StringAssert.Contains(cmd, "exit $rc");
    }

    private sealed class FakeSsh : ISshRunner
    {
        public List<string> Commands { get; } = [];
        public Task<int> RunAsync(string host, string username, string keyPath, string command,
            Func<string, Task>? onOutput = null, Func<string, Task>? onStderr = null, CancellationToken ct = default)
        { Commands.Add(command); return Task.FromResult(0); }
    }

    [TestMethod]
    public async Task Deploy_StillIsOneSshCallForTheComposeStage()
    {
        var ssh = new FakeSsh();
        await new RemoteServerDeployer(ssh, new RemoteHealthChecker(ssh, 1, (_, _) => Task.CompletedTask))
            .DeployAsync(Server(), _ => Task.CompletedTask);
        Assert.AreEqual(1, ssh.Commands.Count(c => c.Contains("docker compose up -d")));
    }
}
