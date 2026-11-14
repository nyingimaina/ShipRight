using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.JitDeploy;
using ShipRight.Modules.Projects;

namespace ShipRight.Tests.Modules.JitDeploy;

/// <summary>
/// A secret never travels with a build-on-server deploy: an address carrying a password or token is refused everywhere,
/// the server's saved git remote never holds one, and every line of log and every message passes through a scrubber.
/// </summary>
[TestClass]
public class JitSecretsTests
{
    private const string GhToken = "ghp_a1B2c3D4e5F6g7H8i9J0k1L2m3N4o5P6q7R8";
    private const string Leaky = "https://me:" + GhToken + "@github.com/o/r.git";

    // ── the scrubber ─────────────────────────────────────────────────────────

    [DataTestMethod]
    [DataRow("token ghp_a1B2c3D4e5F6g7H8i9J0k1L2m3N4o5P6q7R8 here", "token *** here")]
    [DataRow("github_pat_11ABCDEFG0abcdefghijklmnopqrstuvwxyz_abcdefghijklmnopqrstuv", "***")]
    [DataRow("glpat-abcdefghij1234567890", "***")]
    [DataRow("key AKIAIOSFODNN7EXAMPLE used", "key *** used")]
    [DataRow("ghp_one_a1B2c3D4e5F6g7H8i9J0k1L2m3N4 and ghp_two_a1B2c3D4e5F6g7H8i9J0k1L2m3N4", "ghp_one_a1B2c3D4e5F6g7H8i9J0k1L2m3N4 and ghp_two_a1B2c3D4e5F6g7H8i9J0k1L2m3N4")]
    public void Redact_HidesKnownTokenShapes_AnywhereInTheText(string input, string expected) =>
        Assert.AreEqual(expected, GitAddressForms.Redact(input));

    [TestMethod]
    public void Redact_HidesTwoTokensInOneLine() =>
        Assert.AreEqual("*** and ***", GitAddressForms.Redact($"{GhToken} and glpat-abcdefghij1234567890"));

    [DataTestMethod]
    [DataRow("the ghp_ prefix is short")]
    [DataRow("ghp_short")]
    [DataRow("a glpat- word")]
    [DataRow("AKIA is a prefix")]
    [DataRow("release r3-abc1234 built")]
    public void Redact_LeavesOrdinaryTextAlone(string input) =>
        Assert.AreEqual(input, GitAddressForms.Redact(input));

    [TestMethod]
    public void Redact_StillHidesPasswordsInsideAddresses() =>
        Assert.AreEqual("fatal: could not read 'https://***@github.com/o/r.git/'", GitAddressForms.Redact("fatal: could not read '" + Leaky + "/'"));

    // ── an address with a secret is refused everywhere ───────────────────────

    [DataTestMethod]
    [DataRow(Leaky)]
    [DataRow("https://" + GhToken + "@github.com/o/r.git")]
    [DataRow("ssh://git:hunter2@host/o/r.git")]
    public void TheGitAddressRule_RefusesASecret_WithoutRepeatingIt(string url)
    {
        var ex = Assert.ThrowsException<ArgumentException>(() => JitNames.RepoUrl(url));
        StringAssert.Contains(ex.Message, "password or token");
        StringAssert.Contains(ex.Message, "deploy key");
        Assert.IsFalse(ex.Message.Contains(GhToken) || ex.Message.Contains("hunter2"));
    }

    [DataTestMethod]
    [DataRow("git@github.com:o/r.git")]
    [DataRow("ssh://git@host/o/r.git")]
    [DataRow("https://github.com/o/r.git")]
    [DataRow("https://git.corp:8443/o/r.git")]
    public void OrdinaryAddressesStillPass(string url) => Assert.AreEqual(url, JitNames.RepoUrl(url));

    [TestMethod]
    public void NoCommandCanBeBuiltFromAnAddressWithASecret()
    {
        var t = new JitTarget("h", "u", "/k", "/home/u/shop");
        Assert.ThrowsException<ArgumentException>(() => JitCommands.CheckGit(Leaky));
        Assert.ThrowsException<ArgumentException>(() => JitCommands.SyncRepo(t, Leaky));
    }

    [TestMethod]
    public void SavingSettings_WithASecretInTheAddress_IsRefused_WithAPlainMessage()
    {
        var problems = JitDeployConfigValidator.Validate(new JitDeployConfig { Enabled = true, RepoUrl = Leaky, Branch = "main" });
        var p = problems.Single(x => x.Field == "repoUrl");
        StringAssert.Contains(p.Message, "password or token");
        Assert.IsFalse(p.Message.Contains(GhToken));
    }

    private static ProjectConfig Project() => new()
    {
        Id = "p1", Name = "Shop",
        Services = [new ServiceConfig { Name = "api", DockerImageName = "shop/api" }],
        Server = new ServerConfig { Host = "h", Username = "u", SshKeyPath = "/k", RemoteWorkingDir = "/home/u/shop" },
    };

    [TestMethod]
    public void AnOldSavedSettingWithASecret_BlocksTheDeploy_AndSaysWhy()
    {
        var r = JitDeployMapper.Map(Project(), new JitDeployConfig { Enabled = true, RepoUrl = Leaky, Branch = "main" });
        Assert.IsNull(r.Request);
        var problem = r.Problems.Single(x => x.Contains("password or token"));
        Assert.IsFalse(problem.Contains(GhToken));
    }

    // ── the settings screen never shows a saved secret ───────────────────────

    private sealed class MemoryProjects : IProjectStore
    {
        public Dictionary<string, ProjectConfig> All { get; } = [];
        public int Count => All.Count;
        public Task<ProjectConfig?> GetByNameAsync(string name) => Task.FromResult(All.Values.FirstOrDefault(p => p.Name == name));
        public Task<List<ProjectConfig>> GetAllAsync() => Task.FromResult(All.Values.ToList());
        public Task<ProjectConfig?> GetByIdAsync(string id) => Task.FromResult(All.GetValueOrDefault(id));
        public Task SaveAsync(ProjectConfig p) { All[p.Id] = p; return Task.CompletedTask; }
        public Task DeleteAsync(string id) { All.Remove(id); return Task.CompletedTask; }
    }

    private sealed class MemoryConfigs : IJitConfigStore
    {
        public Dictionary<string, JitDeployConfig> All { get; } = [];
        public Task<JitDeployConfig?> GetAsync(string id) => Task.FromResult(All.GetValueOrDefault(id));
        public Task SaveAsync(string id, JitDeployConfig c) { All[id] = c; return Task.CompletedTask; }
        public Task DeleteAsync(string id) { All.Remove(id); return Task.CompletedTask; }
    }

    [TestMethod]
    public async Task TheSettingsScreen_ShowsASavedAddressWithTheSecretRemoved_AndSaysWhy()
    {
        var projects = new MemoryProjects(); await projects.SaveAsync(Project());
        var configs = new MemoryConfigs(); await configs.SaveAsync("p1", new JitDeployConfig { Enabled = true, RepoUrl = Leaky, Branch = "main" });

        var r = await JitDeployRoutes.HandleGetConfigAsync("p1", projects, configs);
        var json = JsonSerializer.Serialize(((IValueHttpResult)r).Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.IsFalse(json.Contains(GhToken), "the secret must not be sent to the screen");
        StringAssert.Contains(json, "\"repoUrl\":\"https://github.com/o/r.git\"");
        StringAssert.Contains(json, "password or token");
    }

    [TestMethod]
    public async Task SavingFromTheScreen_WithASecret_IsA400_WithoutEchoingIt()
    {
        var projects = new MemoryProjects(); await projects.SaveAsync(Project());
        var configs = new MemoryConfigs();

        var r = await JitDeployRoutes.HandleSaveConfigAsync("p1", new JitDeployConfig { Enabled = true, RepoUrl = Leaky, Branch = "main" }, projects, configs);

        Assert.AreEqual(400, ((IStatusCodeHttpResult)r).StatusCode);
        Assert.IsFalse(JsonSerializer.Serialize(((IValueHttpResult)r).Value).Contains(GhToken));
        Assert.AreEqual(0, configs.All.Count, "nothing is saved");
    }

    // ── the whole deploy ─────────────────────────────────────────────────────

    private static readonly JitTarget Target = new("h", "u", "/k", "/home/deploy/shop");

    private static JitDeployRequest Req(string url = "git@github.com:o/r.git") => new()
    {
        Target = Target, Project = "shop", RepoUrl = url, Branch = "master",
        Services = [new JitService("api", "API_TAG", "shop/api"), new JitService("web", "WEB_TAG", "shop/web")],
    };

    private static async Task<(JitDeployResult Result, FakeRemote Remote, List<string> Lines)> Deploy(Action<FakeRemote>? setup = null, string? url = null)
    {
        var remote = new FakeRemote();
        setup?.Invoke(remote);
        var lines = new List<string>();
        var r = await new JitDeployer(remote, new FakeClock(), new JitDeployLock())
            .DeployAsync(url is null ? Req() : Req(url), l => { lock (lines) lines.Add(l); return Task.CompletedTask; });
        return (r, remote, lines);
    }

    [TestMethod]
    public async Task ADeployWhoseAddressCarriesASecret_IsRefusedBeforeAnythingIsSent()
    {
        var (r, remote, lines) = await Deploy(url: Leaky);

        Assert.AreEqual(JitOutcome.FailedBeforeChange, r.Outcome);
        StringAssert.Contains(r.Message, "password or token");
        Assert.AreEqual(0, remote.Commands.Count, "the server is never contacted");
        Assert.IsFalse((r.Message + string.Join("\n", lines)).Contains(GhToken));
    }

    [TestMethod]
    public async Task RecoverAndRollbackBack_AreRefusedTheSameWay()
    {
        var remote = new FakeRemote();
        var deployer = new JitDeployer(remote, new FakeClock(), new JitDeployLock());
        var recover = await deployer.RecoverAsync(Req(Leaky), _ => Task.CompletedTask);
        var rollback = await deployer.RollbackToAsync(Req(Leaky), "r1-abcdef1", _ => Task.CompletedTask);

        Assert.AreEqual(JitOutcome.FailedBeforeChange, recover.Outcome);
        Assert.AreEqual(JitOutcome.FailedBeforeChange, rollback.Outcome);
        Assert.AreEqual(0, remote.Commands.Count);
    }

    [TestMethod]
    public async Task AGitErrorThatEchoesASecret_IsScrubbedFromTheLogAndTheResult()
    {
        var (r, _, lines) = await Deploy(f =>
        {
            f.SyncOk = false;
            f.SyncFailureLines = ["fatal: unable to access '" + Leaky + "/': The requested URL returned error: 403"];
        });

        Assert.AreEqual(JitOutcome.FailedBeforeChange, r.Outcome);
        var everything = r.Message + "\n" + string.Join("\n", lines);
        Assert.IsFalse(everything.Contains(GhToken), everything);
        Assert.IsFalse(everything.Contains("me:"), everything);
        StringAssert.Contains(everything, "https://***@github.com/o/r.git");
    }

    [TestMethod]
    public async Task ATokenInTheBuildOutput_IsScrubbedFromTheLiveLog()
    {
        var (r, _, lines) = await Deploy(f => f.ExtraBuildLines = ["#4 RUN echo using " + GhToken, "#5 done"]);

        Assert.AreEqual(JitOutcome.Deployed, r.Outcome, r.Message);
        var log = string.Join("\n", lines);
        Assert.IsFalse(log.Contains(GhToken));
        StringAssert.Contains(log, "#4 RUN echo using ***");
        StringAssert.Contains(log, "#5 done");
    }

    [TestMethod]
    public async Task OrdinaryLogLines_AreNotChanged()
    {
        var (r, _, lines) = await Deploy();
        Assert.AreEqual(JitOutcome.Deployed, r.Outcome, r.Message);
        Assert.IsTrue(lines.Any(l => l.Contains("Commit aaaaaaa will be release r1-aaaaaaa")));
    }

    [TestMethod]
    public async Task TheCommandsThatReachTheServer_NeverCarryASecret()
    {
        var (_, remote, _) = await Deploy();
        Assert.IsFalse(remote.Commands.Any(c => c.Contains(GhToken) || System.Text.RegularExpressions.Regex.IsMatch(c, @"://[^/\s']+@")),
            "a clean address is sent as it is");
        Assert.IsTrue(remote.Commands.Any(c => c.Contains("remote set-url origin 'git@github.com:o/r.git'") || c.Contains("git clone")),
            "the saved remote is always rewritten to the clean address");
    }
}
