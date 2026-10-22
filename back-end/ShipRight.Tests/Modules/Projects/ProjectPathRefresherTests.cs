using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using ShipRight.Modules.Projects;

namespace ShipRight.Tests.Modules.Projects;

[TestClass]
public class ProjectPathRefresherTests
{
    private const string OldContext = @"D:\work\dailies-backend\Lattice.Services.Foundation\Lattice.Services.Foundation";
    private const string NewContext = @"D:\work\dailies-backend\Lattice.Services.Foundation";
    private const string VersionFile = @"D:\work\dailies-backend\Lattice.Services.Foundation\Lattice.Services.Foundation\version.txt";

    private static ServiceConfig Lattice(string versionFile = VersionFile, string context = OldContext) => new()
    {
        Name = "Lattice Services Foundation",
        VersionFilePath = versionFile,
        BuildContextPath = context,
        DockerImageName = "nyingi/lattice-foundation",
        ComposeServiceName = "lattice-foundation",
        DockerUsername = "registry-user",
        DockerPassword = "registry-s3cret",
        DockerRegistryResourceId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        ImageRetentionCount = 7,
        LocalImageKeepCount = 3,
    };

    private static DetectedService Found(
        string name = "Lattice Services Foundation",
        string versionFile = VersionFile,
        string context = NewContext,
        string? image = null,
        string? compose = null) =>
        new(name, versionFile, context, image, image is not null, compose, "1.17.133");

    private static DetectedProjectConfig Detected(params DetectedService[] services) =>
        new("dailies backend", services.ToList(), [], null, [], []);

    private static ProjectConfig Project(params ServiceConfig[] services) => new()
    {
        Id = "lattice",
        Name = "Lattice",
        Services = services.ToList(),
    };

    private static string Json(object o) => JsonConvert.SerializeObject(o);

    // ── Plan: matching ───────────────────────────────────────────────────────

    [TestMethod]
    public void Plan_MatchesByVersionFile_AndReportsTheNewBuildContext()
    {
        var plan = ProjectPathRefresher.Plan(Project(Lattice()), Detected(Found()));

        Assert.AreEqual(1, plan.Changes.Count);
        var c = plan.Changes[0];
        Assert.AreEqual("Lattice Services Foundation", c.ServiceName);
        Assert.AreEqual(OldContext, c.OldBuildContextPath);
        Assert.AreEqual(NewContext, c.NewBuildContextPath);
        Assert.AreEqual(VersionFile, c.OldVersionFilePath);
        Assert.AreEqual(VersionFile, c.NewVersionFilePath);
    }

    [TestMethod]
    public void Plan_HasNoChangesWhenPathsAlreadyMatch()
    {
        var plan = ProjectPathRefresher.Plan(Project(Lattice(context: NewContext)), Detected(Found()));

        Assert.AreEqual(0, plan.Changes.Count);
        Assert.AreEqual(0, plan.NotFoundInFolder.Count);
        Assert.AreEqual(0, plan.NewInFolder.Count);
    }

    [TestMethod]
    public void Plan_ComparesPathsIgnoringSeparatorsAndCase()
    {
        var sameButWritten = "d:/WORK/dailies-backend/lattice.services.foundation/";
        var plan = ProjectPathRefresher.Plan(
            Project(Lattice(context: NewContext)),
            Detected(Found(context: sameButWritten)));

        Assert.AreEqual(0, plan.Changes.Count);
    }

    [TestMethod]
    public void Plan_MatchesByImageName_WhenTheVersionFileMoved()
    {
        var plan = ProjectPathRefresher.Plan(
            Project(Lattice(versionFile: @"D:\old\place\version.txt")),
            Detected(Found(name: "Something Else", image: "nyingi/lattice-foundation")));

        Assert.AreEqual(1, plan.Changes.Count);
        Assert.AreEqual(VersionFile, plan.Changes[0].NewVersionFilePath);
    }

    [TestMethod]
    public void Plan_MatchesByComposeServiceName_WhenNothingElseMatches()
    {
        var plan = ProjectPathRefresher.Plan(
            Project(Lattice(versionFile: @"D:\old\place\version.txt")),
            Detected(Found(name: "Something Else", compose: "lattice-foundation")));

        Assert.AreEqual(1, plan.Changes.Count);
    }

    [TestMethod]
    public void Plan_MatchesByNameIgnoringCaseAndPunctuation_AsALastResort()
    {
        var existing = Lattice(versionFile: @"D:\old\place\version.txt") with { DockerImageName = "x/y", ComposeServiceName = "" };
        var plan = ProjectPathRefresher.Plan(
            Project(existing),
            Detected(Found(name: "lattice.services-foundation")));

        Assert.AreEqual(1, plan.Changes.Count);
    }

    [TestMethod]
    public void Plan_UsesEachDetectedServiceOnlyOnce()
    {
        var a = Lattice() with { Name = "A" };
        var b = Lattice() with { Name = "B" };

        var plan = ProjectPathRefresher.Plan(Project(a, b), Detected(Found()));

        Assert.AreEqual(1, plan.Changes.Count);
        Assert.AreEqual(1, plan.NotFoundInFolder.Count);
    }

    [TestMethod]
    public void Plan_ReportsServicesThatMatchNothing_InBothDirections()
    {
        var plan = ProjectPathRefresher.Plan(
            Project(Lattice() with { Name = "Old Only", VersionFilePath = @"D:\gone\version.txt", DockerImageName = "gone/img", ComposeServiceName = "gone" }),
            Detected(Found(name: "Brand New", versionFile: @"D:\new\version.txt", context: @"D:\new")));

        Assert.AreEqual(0, plan.Changes.Count);
        CollectionAssert.AreEqual(new[] { "Old Only" }, plan.NotFoundInFolder);
        CollectionAssert.AreEqual(new[] { "Brand New" }, plan.NewInFolder);
    }

    // ── Apply: nothing but the two path fields may change ────────────────────

    private static ProjectConfig RichProject() => new()
    {
        Id = "lattice",
        Name = "Lattice",
        ServerId = "srv-1",
        Type = ProjectType.Pipeline,
        Features = new FreeformFeatures { Docker = true, Git = true, Deploy = true, Database = true },
        Services = [Lattice(), Lattice() with { Name = "Other", VersionFilePath = @"D:\o\version.txt", BuildContextPath = @"D:\o", DockerImageName = "o/o", DockerPassword = "other-pass" }],
        GitRepos = [new GitConfig { RepoPath = @"D:\work\dailies-backend", DeployBranch = "master", PushArgs = "--no-verify", CredentialResourceId = Guid.Parse("22222222-2222-2222-2222-222222222222") }],
        Wsl = new WslConfig { WorkingDir = "/home/nyingi/work/docker", ComposeRepoUrl = "https://example.test/compose.git" },
        Server = new ServerConfig
        {
            Id = "srv-1", Name = "prod", Host = "1.2.3.4", Username = "ubuntu", SshKeyPath = "/k.pem",
            RemoteWorkingDir = "/app", RebuildScript = "rebuild.sh", DeployMode = DeployMode.EnvCompose,
            ManagedSshKey = true, ManagedSshKeyAuthorized = true,
            PipelineResourceId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
            ComposeRepoUrl = "https://example.test/c.git", EcrRegion = "us-east-2", EcrRegistry = "123.dkr.ecr.us-east-2.amazonaws.com",
            EnvFile = "SECRET=1", HealthCheckUrl = "http://x/health", SkipHealthCheck = true,
        },
        Database = new DatabaseConfig { ContainerName = "db", DatabaseName = "app", RootUser = "root", RootPassword = "db-s3cret", BackupRetainCount = 4 },
        CreatedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
        TimeZone = "Africa/Nairobi",
        WatchBranch = "master",
        WatchPollSeconds = 120,
        WatchSteps = "BuildAndPush",
        GitPushTimeoutSeconds = 99,
        LocalCachePruneKeepGb = 11,
    };

    [TestMethod]
    public void Apply_ChangesOnlyThePathsOfTheMatchedService_AndNothingElseInTheProject()
    {
        var original = RichProject();
        var plan = ProjectPathRefresher.Plan(original, Detected(Found()));

        var result = ProjectPathRefresher.Apply(original, plan);

        Assert.AreEqual(NewContext, result.Services[0].BuildContextPath);
        // Undo the one intended change; the whole project must then be identical, secrets included.
        var undone = result with
        {
            Services = result.Services.Select((s, i) => i == 0 ? s with { BuildContextPath = OldContext } : s).ToList(),
        };
        Assert.AreEqual(Json(original), Json(undone));
    }

    [TestMethod]
    public void Apply_KeepsEverySecretExactlyAsStored()
    {
        var original = RichProject();
        var plan = ProjectPathRefresher.Plan(original, Detected(Found()));

        var result = ProjectPathRefresher.Apply(original, plan);

        Assert.AreEqual("registry-s3cret", result.Services[0].DockerPassword);
        Assert.AreEqual("registry-user", result.Services[0].DockerUsername);
        Assert.AreEqual("other-pass", result.Services[1].DockerPassword);
        Assert.AreEqual("db-s3cret", result.Database!.RootPassword);
        Assert.AreEqual("SECRET=1", result.Server.EnvFile);
        Assert.AreEqual("https://example.test/compose.git", result.Wsl.ComposeRepoUrl);
    }

    [TestMethod]
    public void Apply_NeverChangesAServiceName_BecauseCredentialsAreMatchedByName()
    {
        var original = RichProject();
        var plan = ProjectPathRefresher.Plan(original, Detected(Found(name: "A Different Suggested Name")));

        var result = ProjectPathRefresher.Apply(original, plan);

        CollectionAssert.AreEqual(
            original.Services.Select(s => s.Name).ToList(),
            result.Services.Select(s => s.Name).ToList());
    }

    [TestMethod]
    public void Apply_WithNoChanges_ReturnsAnIdenticalProject()
    {
        var original = RichProject();
        var noChanges = new PathRefreshPlan([], [], []);

        var result = ProjectPathRefresher.Apply(original, noChanges);

        Assert.AreEqual(Json(original), Json(result));
    }

    [TestMethod]
    public void Apply_RefusesAChangeForAServiceThatIsNotInTheProject()
    {
        var plan = new PathRefreshPlan(
            [new PathChange("Ghost", "a", "b", "c", "d")], [], []);

        Assert.ThrowsException<InvalidOperationException>(() => ProjectPathRefresher.Apply(RichProject(), plan));
    }
}
