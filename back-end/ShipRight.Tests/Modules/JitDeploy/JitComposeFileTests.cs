using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.JitDeploy;
using ShipRight.Modules.Projects;

namespace ShipRight.Tests.Modules.JitDeploy;

/// <summary>The compose file does not have to be in the root of the repository: a path inside it can be named. Empty keeps the old behaviour exactly.</summary>
[TestClass]
public class JitComposeFileTests
{
    private static readonly JitTarget Target = new("h", "u", "/k", "/home/deploy/shop");
    private static readonly IReadOnlyList<TagBinding> Bindings = [new TagBinding("API_TAG", "shop/api")];

    // ── the rule ─────────────────────────────────────────────────────────────

    [DataTestMethod]
    [DataRow("docker-compose.yml")]
    [DataRow("deploy/docker-compose.yml")]
    [DataRow("ops/prod/compose.yaml")]
    [DataRow("a_b-c/d.e/compose.prod.yml")]
    public void SensibleRepositoryPathsAreAccepted(string path) => Assert.AreEqual(path, JitNames.RepoFile(path));

    [DataTestMethod]
    [DataRow("")]
    [DataRow("/etc/compose.yml")]
    [DataRow("../compose.yml")]
    [DataRow("a/../../compose.yml")]
    [DataRow("deploy//compose.yml")]
    [DataRow("compose.txt")]
    [DataRow("my compose.yml")]
    [DataRow("a;b.yml")]
    [DataRow("-f.yml")]
    [DataRow("a\nb.yml")]
    [DataRow("$(x).yml")]
    public void AnythingElseIsRefused(string path) => Assert.ThrowsException<ArgumentException>(() => JitNames.RepoFile(path));

    // ── the commands ─────────────────────────────────────────────────────────

    private static ComposeContext Ctx(string? file) => new(Target, "shop", null, file);

    [TestMethod]
    public void EveryComposeCommand_UsesTheNamedFile_AfterTheStackName()
    {
        var ctx = Ctx("deploy/docker-compose.yml");
        var all = new[]
        {
            JitCommands.ComposeConfig(ctx, Bindings, "r1-0000000"),
            JitCommands.Build(ctx, ["api"], Bindings, "r1-0000000"),
            JitCommands.StopService(ctx, "api", 30, Bindings, "r1-0000000"),
            JitCommands.StartService(ctx, "api", Bindings, "r1-0000000"),
            JitCommands.ProbeService(ctx, "api", Bindings, "r1-0000000"),
        };
        foreach (var c in all) StringAssert.Contains(c, "docker compose -p 'shop' -f 'deploy/docker-compose.yml' ", c);
    }

    [TestMethod]
    public void WithNoFile_TheCommandsAreExactlyWhatTheyAlwaysWere()
    {
        var plain = Ctx(null);
        foreach (var c in new[] { JitCommands.ComposeConfig(plain, Bindings, "r1-0000000"), JitCommands.Build(plain, ["api"], Bindings, "r1-0000000") })
        {
            Assert.IsFalse(c.Contains(" -f "), c);
            StringAssert.Contains(c, "docker compose -p 'shop' ");
        }
    }

    [TestMethod]
    public void TheFileGoesBeforeTheEnvFile_AndAnUnsafeNameNeverBuildsACommand()
    {
        var c = JitCommands.Build(new ComposeContext(Target, "shop", "/home/u/.env", "deploy/c.yml"), ["api"], Bindings, "r1-0000000");
        StringAssert.Contains(c, "-p 'shop' -f 'deploy/c.yml' --env-file '/home/u/.env' build");
        Assert.ThrowsException<ArgumentException>(() => JitCommands.Build(Ctx("../x.yml"), ["api"], Bindings, "r1-0000000"));
    }

    // ── settings, mapper, deploy ─────────────────────────────────────────────

    [TestMethod]
    public void TheSettingsRule_NamesTheField()
    {
        var bad = JitDeployConfigValidator.Validate(new JitDeployConfig { Enabled = true, RepoUrl = "git@h:o/r.git", Branch = "main", ComposeFile = "../x.yml" });
        var p = bad.Single(x => x.Field == "composeFile");
        StringAssert.Contains(p.Message, "compose file");
        Assert.AreEqual(0, JitDeployConfigValidator.Validate(new JitDeployConfig { Enabled = true, RepoUrl = "git@h:o/r.git", Branch = "main", ComposeFile = "deploy/docker-compose.yml" }).Count(x => x.Field == "composeFile"));
        Assert.AreEqual(0, JitDeployConfigValidator.Validate(new JitDeployConfig { Enabled = true, RepoUrl = "git@h:o/r.git", Branch = "main", ComposeFile = "  " }).Count(x => x.Field == "composeFile"));
    }

    private static ProjectConfig Project() => new()
    {
        Id = "p1", Name = "Shop",
        Services = [new ServiceConfig { Name = "api", DockerImageName = "shop/api" }],
        Server = new ServerConfig { Host = "h", Username = "u", SshKeyPath = "/k", RemoteWorkingDir = "/home/u/shop" },
    };

    [TestMethod]
    public void TheMapperPassesItOn_TrimmedAndEmptyMeansNone()
    {
        Assert.AreEqual("deploy/c.yml", JitDeployMapper.Map(Project(), new JitDeployConfig { Enabled = true, RepoUrl = "git@h:o/r.git", Branch = "main", ComposeFile = " deploy/c.yml " }).Request!.ComposeFile);
        Assert.IsNull(JitDeployMapper.Map(Project(), new JitDeployConfig { Enabled = true, RepoUrl = "git@h:o/r.git", Branch = "main", ComposeFile = "" }).Request!.ComposeFile);
    }

    private static JitDeployRequest Req(string? file) => new()
    {
        Target = Target, Project = "shop", RepoUrl = "git@github.com:o/r.git", Branch = "master", ComposeFile = file,
        Services = [new JitService("api", "API_TAG", "shop/api"), new JitService("web", "WEB_TAG", "shop/web")],
    };

    [TestMethod]
    public async Task ADeploy_UsesTheNamedFileForEveryComposeCommand()
    {
        var remote = new FakeRemote();
        var r = await new JitDeployer(remote, new FakeClock(), new JitDeployLock()).DeployAsync(Req("deploy/docker-compose.yml"), _ => Task.CompletedTask);
        Assert.AreEqual(JitOutcome.Deployed, r.Outcome, r.Message);

        var compose = remote.Commands.Where(c => c.Contains("docker compose -p")).ToList();
        Assert.IsTrue(compose.Count > 5);
        Assert.IsTrue(compose.All(c => c.Contains("-f 'deploy/docker-compose.yml'")), string.Join("\n", compose.Where(c => !c.Contains("-f 'deploy/"))));
    }

    [TestMethod]
    public async Task WithoutIt_NoComposeCommandChanges()
    {
        var remote = new FakeRemote();
        await new JitDeployer(remote, new FakeClock(), new JitDeployLock()).DeployAsync(Req(null), _ => Task.CompletedTask);
        Assert.IsFalse(remote.Commands.Any(c => System.Text.RegularExpressions.Regex.IsMatch(c, "docker compose -p '[^']*' -f ")));
    }

    [TestMethod]
    public async Task AnUnsafeFile_IsRefusedBeforeTheServerIsContacted()
    {
        var remote = new FakeRemote();
        var r = await new JitDeployer(remote, new FakeClock(), new JitDeployLock()).DeployAsync(Req("../../etc/passwd.yml"), _ => Task.CompletedTask);
        Assert.AreEqual(JitOutcome.FailedBeforeChange, r.Outcome);
        Assert.AreEqual(0, remote.Commands.Count);
    }
}
