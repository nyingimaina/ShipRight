using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.JitDeploy;
using ShipRight.Modules.Projects;

namespace ShipRight.Tests.Modules.JitDeploy;

/// <summary>
/// The server's variables and the project's own reach compose on every build-on-server deploy: merged (the project
/// wins), written beside the deploy state before anything is built, and handed over with --env-file. A file the person
/// keeps on the server themselves is used as it is, and nothing is written.
/// </summary>
[TestClass]
public class JitEnvDeployTests
{
    private static readonly JitTarget Target = new("h", "u", "/k", "/home/deploy/shop");
    private const string EnvPath = "/home/deploy/shop/.deploy-state/deploy.env";

    private static ProjectConfig Project(string serverEnv) => new()
    {
        Id = "p1", Name = "Shop",
        Services = [new ServiceConfig { Name = "api", DockerImageName = "shop/api" }],
        Server = new ServerConfig { Host = "h", Username = "u", SshKeyPath = "/k", RemoteWorkingDir = "/home/deploy/shop", EnvFile = serverEnv },
    };

    private static JitDeployConfig Cfg(string? envText = null, string envFile = "") => new()
    {
        Enabled = true, RepoUrl = "git@h:o/r.git", Branch = "main", EnvText = envText ?? string.Empty, EnvFile = envFile,
    };

    // ── the mapper ───────────────────────────────────────────────────────────

    [TestMethod]
    public void Map_MergesBothSources_ProjectWinning_AndPointsComposeAtTheGeneratedFile()
    {
        var r = JitEnvMapper.Map(Project("DB=server\nLOG=info"), Cfg("LOG=debug")).Request!;
        Assert.AreEqual(EnvPath, r.EnvFile);
        StringAssert.Contains(r.EnvContent!, "DB=server");
        StringAssert.Contains(r.EnvContent!, "LOG=debug");
        Assert.IsFalse(r.EnvContent!.Contains("LOG=info"));
        StringAssert.Contains(r.EnvNote!, "LOG");
    }

    [TestMethod]
    public void Map_WithNoVariablesAnywhere_AddsNothing_SoDeploysAreAsTheyWereBefore()
    {
        var r = JitEnvMapper.Map(Project(""), Cfg()).Request!;
        Assert.IsNull(r.EnvFile);
        Assert.IsNull(r.EnvContent);
        Assert.IsNull(r.EnvNote);
    }

    [TestMethod]
    public void Map_AFileOnTheServerChosenByPath_IsUsedAsItIs_NothingIsMergedOrWritten()
    {
        var r = JitEnvMapper.Map(Project("DB=server"), Cfg("LOG=debug", "/etc/shop/.env")).Request!;
        Assert.AreEqual("/etc/shop/.env", r.EnvFile);
        Assert.IsNull(r.EnvContent);
    }

    [TestMethod]
    public void Map_OnlyTheServersVariables_AreStillWritten()
    {
        var r = JitEnvMapper.Map(Project("DB=server"), Cfg()).Request!;
        Assert.AreEqual(EnvPath, r.EnvFile);
        StringAssert.Contains(r.EnvContent!, "DB=server");
    }

    [TestMethod]
    public void Map_KeepsEveryProblemTheOriginalMapperReports()
    {
        var map = JitEnvMapper.Map(Project("DB=1"), new JitDeployConfig { Enabled = false });
        Assert.IsFalse(map.Ok);
        Assert.AreEqual(JitDeployMapper.Map(Project("DB=1"), new JitDeployConfig { Enabled = false }).Problems[0], map.Problems[0]);
    }

    [TestMethod]
    public void TheOriginalMapper_IsUnchanged_ItNeverCarriesVariables()
    {
        var r = JitDeployMapper.Map(Project("DB=server"), Cfg("LOG=debug")).Request!;
        Assert.IsNull(r.EnvContent);
        Assert.IsNull(r.EnvFile);
    }

    // ── the deploy ───────────────────────────────────────────────────────────

    private static JitDeployRequest Req(string? content, string? note = "note") => new()
    {
        Target = Target, Project = "shop", RepoUrl = "git@github.com:o/r.git", Branch = "master",
        EnvFile = content is null ? null : EnvPath, EnvContent = content, EnvNote = content is null ? null : note,
        Services = [new JitService("api", "API_TAG", "shop/api"), new JitService("web", "WEB_TAG", "shop/web")],
    };

    [TestMethod]
    public async Task ADeploy_WritesTheFileBeforeComposeFirstRuns_AndEveryComposeCommandUsesIt()
    {
        var remote = new FakeRemote();
        var lines = new List<string>();
        var r = await new JitDeployer(remote, new FakeClock(), new JitDeployLock())
            .DeployAsync(Req("DB=server\nLOG=debug\n"), l => { lines.Add(l); return Task.CompletedTask; });

        Assert.AreEqual(JitOutcome.Deployed, r.Outcome, r.Message);
        Assert.AreEqual("DB=server\nLOG=debug\n", remote.EnvContent);
        Assert.IsTrue(remote.Events.IndexOf("env-written") < remote.Events.IndexOf("config"), "written before compose first reads it");
        Assert.IsTrue(remote.Events.IndexOf("env-written") > remote.Events.IndexOf("checkout:aaaaaaa"), "after the disposable checkout, so the clean step cannot remove it");

        var compose = remote.Commands.Where(c => c.Contains("docker compose -p")).ToList();
        Assert.IsTrue(compose.Count > 5);
        Assert.IsTrue(compose.All(c => c.Contains($"--env-file '{EnvPath}'")), string.Join("\n", compose.Where(c => !c.Contains("--env-file"))));
    }

    [TestMethod]
    public async Task ADeploy_SaysWhichVariablesCameFromWhere_ButNeverShowsAValue()
    {
        var remote = new FakeRemote();
        var lines = new List<string>();
        await new JitDeployer(remote, new FakeClock(), new JitDeployLock())
            .DeployAsync(Req("DB_PASS=hunter2\n", "2 from the server settings, 1 from this project (overriding: LOG)."), l => { lines.Add(l); return Task.CompletedTask; });

        var all = string.Join("\n", lines);
        StringAssert.Contains(all, "overriding: LOG");
        Assert.IsFalse(all.Contains("hunter2"));
        Assert.IsFalse(remote.Commands.Any(c => c.Contains("hunter2")), "values travel encoded");
    }

    [TestMethod]
    public async Task ADeploy_WithoutVariables_WritesNothing_AndPassesNoEnvFile()
    {
        var remote = new FakeRemote();
        var r = await new JitDeployer(remote, new FakeClock(), new JitDeployLock()).DeployAsync(Req(null), _ => Task.CompletedTask);

        Assert.AreEqual(JitOutcome.Deployed, r.Outcome, r.Message);
        Assert.IsNull(remote.EnvContent);
        Assert.IsFalse(remote.Events.Contains("env-written"));
        Assert.IsFalse(remote.Commands.Any(c => c.Contains("--env-file")));
    }

    [TestMethod]
    public async Task IfTheFileCannotBeWritten_TheDeployStopsBeforeBuilding_AndChangesNothing()
    {
        var remote = new FakeRemote { EnvWriteFails = true };
        var r = await new JitDeployer(remote, new FakeClock(), new JitDeployLock()).DeployAsync(Req("A=1\n"), _ => Task.CompletedTask);

        Assert.AreEqual(JitOutcome.FailedBeforeChange, r.Outcome);
        StringAssert.Contains(r.Message, "variables");
        Assert.IsFalse(remote.Events.Any(e => e.StartsWith("build:") || e.StartsWith("stop:") || e.StartsWith("start:")));
        Assert.AreEqual(0, remote.Running.Count);
        Assert.AreEqual(DeployPhase.Idle, remote.ReadState().Phase);
    }

    [TestMethod]
    public async Task ARunningReleaseIsNeverTouched_WhenTheNewVariablesCannotBeWritten()
    {
        var remote = new FakeRemote();
        var first = await new JitDeployer(remote, new FakeClock(), new JitDeployLock()).DeployAsync(Req("A=1\n"), _ => Task.CompletedTask);
        Assert.AreEqual(JitOutcome.Deployed, first.Outcome, first.Message);
        var running = remote.Running.ToDictionary(k => k.Key, k => k.Value);
        remote.Branches["master"] = FakeRemote.Sha2;
        remote.EnvWriteFails = true;

        var r = await new JitDeployer(remote, new FakeClock(), new JitDeployLock()).DeployAsync(Req("A=2\n"), _ => Task.CompletedTask);

        Assert.AreEqual(JitOutcome.FailedBeforeChange, r.Outcome);
        CollectionAssert.AreEquivalent(running, remote.Running.ToDictionary(k => k.Key, k => k.Value));
    }

    // ── saving the settings ──────────────────────────────────────────────────

    [TestMethod]
    public void TheProjectsVariables_RoundTripThroughTheSettingsFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "jit-env-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var store = new JsonJitConfigStore(dir);
            store.SaveAsync("p1", Cfg("LOG=debug\nQ=\"a b\"")).GetAwaiter().GetResult();
            Assert.AreEqual("LOG=debug\nQ=\"a b\"", new JsonJitConfigStore(dir).GetAsync("p1").GetAwaiter().GetResult()!.EnvText);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void ASettingsFileFromBefore_ReadsWithNoVariables()
    {
        Assert.AreEqual(string.Empty, new JitDeployConfig().EnvText);
    }

    [TestMethod]
    public void Validate_FlagsBadVariableText_OnTheEnvTextField()
    {
        var problems = JitEnvConfigValidator.Validate(Cfg("A=1\u0000"));
        Assert.AreEqual("envText", problems.Single(p => p.Field == "envText").Field);
        Assert.AreEqual(0, JitEnvConfigValidator.Validate(Cfg("A=1")).Count(p => p.Field == "envText"));
    }

    [TestMethod]
    public void Validate_StillReportsEveryOriginalProblem()
    {
        var bad = new JitDeployConfig { Enabled = true, RepoUrl = "", Branch = "main" };
        Assert.AreEqual(JitDeployConfigValidator.Validate(bad).Count, JitEnvConfigValidator.Validate(bad).Count);
    }
}
