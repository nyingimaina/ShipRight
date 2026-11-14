using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.JitDeploy;
using ShipRight.Modules.Projects;

namespace ShipRight.Tests.Modules.JitDeploy;

[TestClass]
public class JitConfigTests
{
    private static JitDeployConfig Full() => new()
    {
        Enabled = true,
        RepoUrl = "git@github.com:o/r.git",
        Branch = "release",
        ComposeProject = "shop",
        EnvFile = "/home/deploy/shop/.env",
        ReleasesToKeep = 4,
        StopGraceSeconds = 45,
        HealthTimeoutSeconds = 90,
        SettleSeconds = 15,
        MinFreeDiskMb = 5000,
        BuildNice = 10,
        DependentChoices = [new JitDependentChoice("web", "proxy", false)],
    };

    // ── defaults ─────────────────────────────────────────────────────────────

    [TestMethod]
    public void Defaults_AreOffAndSensible()
    {
        var c = new JitDeployConfig();
        Assert.IsFalse(c.Enabled);
        Assert.AreEqual("master", c.Branch);
        Assert.AreEqual(3, c.ReleasesToKeep);
        Assert.AreEqual(30, c.StopGraceSeconds);
        Assert.AreEqual(60, c.HealthTimeoutSeconds);
        Assert.AreEqual(10, c.SettleSeconds);
        Assert.AreEqual(3000, c.MinFreeDiskMb);
        Assert.IsNull(c.BuildNice);
        Assert.AreEqual(0, c.DependentChoices.Count);
    }

    // ── store ────────────────────────────────────────────────────────────────

    private static string TempDir() => Path.Combine(Path.GetTempPath(), "jit-cfg-" + Guid.NewGuid().ToString("N")[..8]);

    [TestMethod]
    public async Task Store_ReturnsNothingForAProjectWithNoSettings()
    {
        var dir = TempDir();
        try { Assert.IsNull(await new JsonJitConfigStore(dir).GetAsync("p1")); }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [TestMethod]
    public async Task Store_RoundTripsEveryField_AndKeepsProjectsApart()
    {
        var dir = TempDir();
        try
        {
            var store = new JsonJitConfigStore(dir);
            await store.SaveAsync("p1", Full());
            await store.SaveAsync("p2", new JitDeployConfig { RepoUrl = "git@h:o/other.git" });

            var back = (await new JsonJitConfigStore(dir).GetAsync("p1"))!;   // a fresh instance reads it from disk
            Assert.IsTrue(back.Enabled);
            Assert.AreEqual("release", back.Branch);
            Assert.AreEqual("shop", back.ComposeProject);
            Assert.AreEqual("/home/deploy/shop/.env", back.EnvFile);
            Assert.AreEqual(4, back.ReleasesToKeep);
            Assert.AreEqual(45, back.StopGraceSeconds);
            Assert.AreEqual(90, back.HealthTimeoutSeconds);
            Assert.AreEqual(15, back.SettleSeconds);
            Assert.AreEqual(5000, back.MinFreeDiskMb);
            Assert.AreEqual(10, back.BuildNice);
            Assert.AreEqual(new JitDependentChoice("web", "proxy", false), back.DependentChoices.Single());
            Assert.AreEqual("git@h:o/other.git", (await store.GetAsync("p2"))!.RepoUrl);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [TestMethod]
    public async Task Store_SavingAgainReplacesTheProjectsSettings_AndDeleteRemovesThem()
    {
        var dir = TempDir();
        try
        {
            var store = new JsonJitConfigStore(dir);
            await store.SaveAsync("p1", Full());
            await store.SaveAsync("p1", new JitDeployConfig { Branch = "main" });
            Assert.AreEqual("main", (await store.GetAsync("p1"))!.Branch);
            await store.DeleteAsync("p1");
            Assert.IsNull(await store.GetAsync("p1"));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [TestMethod]
    public async Task Store_ParallelSaves_AreAllKept_AndNoTempFileIsLeftBehind()
    {
        var dir = TempDir();
        try
        {
            var store = new JsonJitConfigStore(dir);
            await Task.WhenAll(Enumerable.Range(0, 20).Select(i => store.SaveAsync($"p{i}", new JitDeployConfig { Branch = $"b{i}" })));
            for (var i = 0; i < 20; i++) Assert.AreEqual($"b{i}", (await store.GetAsync($"p{i}"))!.Branch);
            Assert.AreEqual(1, Directory.GetFiles(dir).Length, "only the settings file remains");
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [TestMethod]
    public async Task Store_ADamagedFile_IsAnError_NeverSilentlyReplaced()
    {
        var dir = TempDir();
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, JsonJitConfigStore.FileName);
            await File.WriteAllTextAsync(file, "{ not json");
            var store = new JsonJitConfigStore(dir);

            await Assert.ThrowsExceptionAsync<InvalidDataException>(() => store.GetAsync("p1"));
            await Assert.ThrowsExceptionAsync<InvalidDataException>(() => store.SaveAsync("p1", Full()));
            Assert.AreEqual("{ not json", await File.ReadAllTextAsync(file), "the damaged file is left for a person to look at");
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    // ── validator ────────────────────────────────────────────────────────────

    private static string[] Fields(JitDeployConfig c) => JitDeployConfigValidator.Validate(c).Select(p => p.Field).ToArray();

    [TestMethod]
    public void AFullConfig_IsValid() => Assert.AreEqual(0, Fields(Full()).Length);

    [TestMethod]
    public void ADisabledConfig_MayBeIncomplete_ButNeverUnsafe()
    {
        Assert.AreEqual(0, Fields(new JitDeployConfig()).Length);
        CollectionAssert.AreEqual(new[] { "branch" }, Fields(new JitDeployConfig { Branch = "a;rm -rf /" }));
    }

    [TestMethod]
    public void AnEnabledConfig_NeedsTheGitAddress()
    {
        CollectionAssert.AreEqual(new[] { "repoUrl" }, Fields(Full() with { RepoUrl = "" }));
        var p = JitDeployConfigValidator.Validate(Full() with { RepoUrl = "" }).Single();
        StringAssert.Contains(p.Message, "git");
    }

    [DataTestMethod]
    [DataRow("branch", "--delete")]
    [DataRow("repoUrl", "--upload-pack=x")]
    [DataRow("composeProject", "Bad Name")]
    [DataRow("envFile", "relative/.env")]
    public void UnsafeText_IsRefused_NamingTheField(string field, string value)
    {
        var c = field switch
        {
            "branch" => Full() with { Branch = value },
            "repoUrl" => Full() with { RepoUrl = value },
            "composeProject" => Full() with { ComposeProject = value },
            _ => Full() with { EnvFile = value },
        };
        CollectionAssert.AreEqual(new[] { field }, Fields(c));
    }

    [TestMethod]
    public void OptionalText_MayBeEmpty()
    {
        Assert.AreEqual(0, Fields(Full() with { ComposeProject = "", EnvFile = "" }).Length);
    }

    [DataTestMethod]
    [DataRow(-1, 30, 60, 10, 3000, "releasesToKeep")]
    [DataRow(101, 30, 60, 10, 3000, "releasesToKeep")]
    [DataRow(3, -1, 60, 10, 3000, "stopGraceSeconds")]
    [DataRow(3, 601, 60, 10, 3000, "stopGraceSeconds")]
    [DataRow(3, 30, 4, 10, 3000, "healthTimeoutSeconds")]
    [DataRow(3, 30, 3601, 10, 3000, "healthTimeoutSeconds")]
    [DataRow(3, 30, 60, -1, 3000, "settleSeconds")]
    [DataRow(3, 30, 60, 601, 3000, "settleSeconds")]
    [DataRow(3, 30, 60, 10, 99, "minFreeDiskMb")]
    public void NumbersOutOfRange_AreRefused(int keep, int grace, int health, int settle, int disk, string field)
    {
        var c = Full() with { ReleasesToKeep = keep, StopGraceSeconds = grace, HealthTimeoutSeconds = health, SettleSeconds = settle, MinFreeDiskMb = disk };
        CollectionAssert.AreEqual(new[] { field }, Fields(c));
    }

    [DataTestMethod]
    [DataRow(-1)]
    [DataRow(20)]
    public void ABuildPriorityOutsideZeroToNineteen_IsRefused(int nice) =>
        CollectionAssert.AreEqual(new[] { "buildNice" }, Fields(Full() with { BuildNice = nice }));

    [TestMethod]
    public void AKeepCountBelowTwo_IsAllowed_ItIsRaisedToTheMinimumWhenUsed() =>
        Assert.AreEqual(0, Fields(Full() with { ReleasesToKeep = 1 }).Length);

    [TestMethod]
    public void DependentChoices_MustNameSafeServices()
    {
        CollectionAssert.AreEqual(new[] { "dependentChoices" },
            Fields(Full() with { DependentChoices = [new JitDependentChoice("web; id", "proxy", true)] }));
    }

    // ── mapping a project and its settings to a deploy request ───────────────

    private static ProjectConfig Project(Func<ServerConfig, ServerConfig>? server = null, params ServiceConfig[] services) => new()
    {
        Id = "p1",
        Name = "My Shop",
        Services = services.Length > 0 ? [.. services] :
        [
            new ServiceConfig { Name = "api", DockerImageName = "shop/api", ComposeServiceName = "api" },
            new ServiceConfig { Name = "web-app", DockerImageName = "shop/web", ComposeServiceName = "" },
        ],
        Server = (server ?? (s => s))(new ServerConfig
        {
            Host = "10.0.0.5", Username = "deploy", SshKeyPath = "/keys/k", RemoteWorkingDir = "/home/deploy/shop/",
        }),
    };

    [TestMethod]
    public void Map_BuildsTheRequestFromTheProjectAndItsSettings()
    {
        var m = JitDeployMapper.Map(Project(), Full());

        Assert.IsTrue(m.Ok, string.Join(" | ", m.Problems));
        var r = m.Request!;
        Assert.AreEqual("10.0.0.5", r.Target.Host);
        Assert.AreEqual("deploy", r.Target.Username);
        Assert.AreEqual("/keys/k", r.Target.SshKeyPath);
        Assert.AreEqual("/home/deploy/shop", r.Target.ProjectDir);
        Assert.AreEqual("git@github.com:o/r.git", r.RepoUrl);
        Assert.AreEqual("release", r.Branch);
        Assert.AreEqual("shop", r.Project);
        Assert.AreEqual("/home/deploy/shop/.env", r.EnvFile);
        Assert.AreEqual(4, r.Settings.ReleasesToKeep);
        Assert.AreEqual(45, r.Settings.StopGraceSeconds);
        Assert.AreEqual(90, r.Settings.HealthTimeoutSeconds);
        Assert.AreEqual(15, r.Settings.SettleSeconds);
        Assert.AreEqual(5000, r.Settings.MinFreeDiskMb);
        Assert.AreEqual(10, r.Settings.BuildNice);
        Assert.IsFalse(r.DependentChoices![("web", "proxy")]);
    }

    [TestMethod]
    public void Map_UsesTheExistingTagVariableConvention_AndComposeServiceNames()
    {
        var r = JitDeployMapper.Map(Project(), Full()).Request!;
        Assert.AreEqual(new JitService("api", "API_TAG", "shop/api"), r.Services[0]);
        // no compose name set: derived from the service name, as the generated compose file does
        Assert.AreEqual(new JitService("web-app", "WEB_APP_TAG", "shop/web"), r.Services[1]);
    }

    [TestMethod]
    public void Map_WithoutAComposeProjectName_DerivesOneFromTheProjectName()
    {
        var r = JitDeployMapper.Map(Project(), Full() with { ComposeProject = "" }).Request!;
        Assert.AreEqual("my-shop", r.Project);
    }

    [TestMethod]
    public void Map_WithoutAnEnvFile_SendsNone()
    {
        Assert.IsNull(JitDeployMapper.Map(Project(), Full() with { EnvFile = "" }).Request!.EnvFile);
    }

    [TestMethod]
    public void Map_CarriesTheOptionsOfThisRun()
    {
        var r = JitDeployMapper.Map(Project(), Full(), commit: "ab8b4a9", force: true, allowWithoutRollback: true).Request!;
        Assert.AreEqual("ab8b4a9", r.Commit);
        Assert.IsTrue(r.Force);
        Assert.IsTrue(r.AllowWithoutRollback);
    }

    [TestMethod]
    public void Map_WhenItIsNotTurnedOn_SaysSo()
    {
        foreach (var cfg in new JitDeployConfig?[] { null, Full() with { Enabled = false } })
        {
            var m = JitDeployMapper.Map(Project(), cfg);
            Assert.IsFalse(m.Ok);
            StringAssert.Contains(m.Problems[0], "not turned on");
        }
    }

    [TestMethod]
    public void Map_ReportsEveryMissingThingInPlainLanguage()
    {
        var p = Project(s => s with { Host = "", RemoteWorkingDir = "" });
        var m = JitDeployMapper.Map(p, Full() with { RepoUrl = "" });
        Assert.IsFalse(m.Ok);
        Assert.IsTrue(m.Problems.Any(x => x.Contains("server")));
        Assert.IsTrue(m.Problems.Any(x => x.Contains("git")));
    }

    [TestMethod]
    public void Map_AServiceWithNoImageName_IsReported()
    {
        var p = Project(null, new ServiceConfig { Name = "api", DockerImageName = "" });
        var m = JitDeployMapper.Map(p, Full());
        Assert.IsFalse(m.Ok);
        StringAssert.Contains(m.Problems.Single(), "api");
    }

    [TestMethod]
    public void Map_AProjectWithNoServices_IsReported()
    {
        var p = Project() with { Services = [] };
        Assert.IsFalse(JitDeployMapper.Map(p, Full()).Ok);
    }

    [TestMethod]
    public void Map_AnImageNameWithCapitals_IsExplained_BecauseDockerWantsLowerCase()
    {
        var p = Project(null, new ServiceConfig { Name = "api", DockerImageName = "Owner/Api", ComposeServiceName = "api" });
        var m = JitDeployMapper.Map(p, Full());
        Assert.IsFalse(m.Ok);
        StringAssert.Contains(m.Problems.Single(), "lower-case");
    }
}
