using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Builds;
using ShipRight.Modules.Projects;
using ShipRight.Shared.ProcessRunner;

namespace ShipRight.Tests.Modules.Projects;

[TestClass]
public class ProjectSpaceServiceTests
{
    private const string Home = "/home/dev";

    private sealed class Scripted : IProcessRunner
    {
        private readonly List<(string Contains, ProcessResult Result)> _rules = [];
        public List<string> Calls { get; } = [];

        public Scripted When(string contains, string stdout = "", int exit = 0, string stderr = "")
        {
            _rules.Insert(0, (contains, new ProcessResult(exit, stdout, stderr, TimeSpan.Zero)));
            return this;
        }

        public Task<ProcessResult> RunAsync(string executable, string[] args, string? workingDir,
            Func<string, Task>? onOutput = null, Func<string, Task>? onError = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? envOverride = null, TimeSpan? timeout = null, string? stdin = null)
        {
            var line = executable + " " + string.Join(" ", args);
            Calls.Add(line);
            foreach (var (contains, result) in _rules)
                if (line.Contains(contains)) return Task.FromResult(result);
            return Task.FromResult(new ProcessResult(0, "", "", TimeSpan.Zero));
        }
    }

    private sealed class Projects(params ProjectConfig[] all) : IProjectStore
    {
        public int Count => all.Length;
        public Task<List<ProjectConfig>> GetAllAsync() => Task.FromResult(all.ToList());
        public Task<ProjectConfig?> GetByIdAsync(string id) => Task.FromResult(all.FirstOrDefault(p => p.Id == id));
        public Task<ProjectConfig?> GetByNameAsync(string name) => Task.FromResult(all.FirstOrDefault(p => p.Name == name));
        public Task SaveAsync(ProjectConfig project) => Task.CompletedTask;
        public Task DeleteAsync(string id) => Task.CompletedTask;
    }

    private sealed class Builds(params BuildRecord[] all) : IBuildStore
    {
        public int Count => all.Length;
        public Task SaveAsync(BuildRecord record) => Task.CompletedTask;
        public Task<BuildRecord?> GetByIdAsync(string id) => Task.FromResult(all.FirstOrDefault(r => r.Id == id));
        public Task<List<BuildRecord>> QueryAsync(string? projectId, string? status, DateTime? from, DateTime? to,
            string? gitTag, int page, int pageSize) =>
            Task.FromResult(all.Where(r => (projectId is null || r.ProjectId == projectId) && (status is null || r.Status.ToString() == status)).ToList());
        public Task<int> CountQueryAsync(string? projectId, string? status, DateTime? from, DateTime? to, string? gitTag) => Task.FromResult(all.Length);
        public Task MarkInterruptedAsync() => Task.CompletedTask;
    }

    private static ProjectConfig Shop(params ServiceConfig[] extra) => new()
    {
        Id = "p1", Name = "Shop",
        Services = [new ServiceConfig { Name = "Api", DockerImageName = "shop/api" }, .. extra],
    };

    private static BuildRecord Build(string id, BuildStatus status, string version, string service = "Api", string project = "p1") =>
        new() { Id = id, ProjectId = project, Status = status, Versions = [new ServiceVersion { ServiceName = service, NewVersion = version, DockerImageName = "shop/api" }] };

    private static string Img(string repo, string tag, string size, string age = "2 days ago") =>
        string.Join("\t", repo, tag, "sha256:" + tag, size, age);

    private static string Du(params (long Kb, string Path)[] rows) => string.Join("\n", rows.Select(r => $"{r.Kb}\t{r.Path}")) + "\n";

    private static string BuildDir(string buildId) => $"{Home}/.shipright/build/p1/{buildId}";

    private static ProjectSpaceService Sut(Scripted r, ProjectConfig? project = null, params BuildRecord[] builds) =>
        new(r, new Projects(project ?? Shop()), new Builds(builds));

    // ── the report ────────────────────────────────────────────────────

    [TestMethod]
    public async Task Report_ListsOnlyThisProjectsImages_AndSaysWhichBuildMadeThem()
    {
        var r = new Scripted()
            .When("images", string.Join("\n", Img("shop/api", "1.0.1", "500MB"), Img("shop/api", "1.0.2", "490MB"), Img("shop/api", "1.0.3", "480MB"),
                Img("redis", "7", "100MB"), Img("shop/other", "1", "50MB")));
        var builds = new[]
        {
            Build("b1", BuildStatus.Deployed, "1.0.1"),
            Build("b2", BuildStatus.BuildFailed, "1.0.2"),
        };

        var report = (await Sut(r, null, builds).ReportAsync("p1", default))!;

        CollectionAssert.AreEquivalent(new[] { "shop/api:1.0.1", "shop/api:1.0.2", "shop/api:1.0.3" }, report.Images.Select(i => i.Reference).ToArray());
        Assert.AreEqual("Successful build", report.Images.Single(i => i.Reference == "shop/api:1.0.1").Origin);
        Assert.AreEqual("Failed build", report.Images.Single(i => i.Reference == "shop/api:1.0.2").Origin);
        Assert.AreEqual("Unknown", report.Images.Single(i => i.Reference == "shop/api:1.0.3").Origin);
    }

    [TestMethod]
    public async Task Report_FlagsImagesAContainerUses_AndDoesNotCountThemAsReclaimable()
    {
        var r = new Scripted()
            .When("images", string.Join("\n", Img("shop/api", "1.0.1", "500MB"), Img("shop/api", "1.0.2", "400MB")))
            .When("ps", "shop/api:1.0.1\n");

        var report = (await Sut(r).ReportAsync("p1", default))!;

        Assert.IsTrue(report.Images.Single(i => i.Reference == "shop/api:1.0.1").InUse);
        Assert.AreEqual(900_000_000L, report.ImagesBytes);
        Assert.AreEqual(400_000_000L, report.ReclaimableBytes);
    }

    [TestMethod]
    public async Task Report_MeasuresLeftoverBuildCopies_AndTheCache_AndKeepsActiveBuildsOutOfReclaimable()
    {
        var r = new Scripted()
            .When("build/p1", Du((2048, BuildDir("old1")), (1024, BuildDir("live"))))
            .When("cache/p1.git", Du((5000, $"{Home}/.shipright/cache/p1.git")));

        var report = (await Sut(r, null, Build("live", BuildStatus.Running, "1.0.5")).ReportAsync("p1", default))!;

        Assert.AreEqual(2, report.Snapshots.Count);
        Assert.IsTrue(report.Snapshots.Single(s => s.BuildId == "live").Active);
        Assert.IsFalse(report.Snapshots.Single(s => s.BuildId == "old1").Active);
        Assert.AreEqual(3072L * 1024, report.SnapshotsBytes);
        Assert.AreEqual(2048L * 1024, report.ReclaimableBytes, "a build that is running keeps its copy");
        Assert.AreEqual(5000L * 1024, report.CacheBytes);
        Assert.AreEqual(report.ImagesBytes + report.SnapshotsBytes + report.CacheBytes, report.TotalBytes);
    }

    [TestMethod]
    public async Task Report_SaysWhichServicesKeepTheirImages_SoThePolicyIsVisible()
    {
        var project = Shop(new ServiceConfig { Name = "Web", DockerImageName = "shop/web", LocalImageKeepCount = 0 },
            new ServiceConfig { Name = "Worker", DockerImageName = "shop/worker", SkipLocalCleanup = true });
        var r = new Scripted().When("images", "");

        var report = (await Sut(r, project).ReportAsync("p1", default))!;

        Assert.AreEqual(2, report.Services.Single(s => s.Name == "Api").KeepCount, "unset means the default of 2");
        Assert.AreEqual(0, report.Services.Single(s => s.Name == "Web").KeepCount);
        Assert.IsTrue(report.Services.Single(s => s.Name == "Worker").NeverClean);
        Assert.IsFalse(report.DeletesAfterPush);
    }

    [TestMethod]
    public async Task Report_DeletesAfterPush_OnlyWhenEveryServiceDoes()
    {
        var all = new ProjectConfig
        {
            Id = "p1", Services = [new ServiceConfig { Name = "A", DockerImageName = "a", LocalImageKeepCount = 0 }, new ServiceConfig { Name = "B", DockerImageName = "b", LocalImageKeepCount = 0 }],
        };
        var report = (await Sut(new Scripted().When("images", ""), all).ReportAsync("p1", default))!;

        Assert.IsTrue(report.DeletesAfterPush);
    }

    [TestMethod]
    public async Task Report_WhenDockerCannotBeReached_SaysSo_InsteadOfClaimingNothingIsHeld()
    {
        var r = new Scripted().When("images", "", 1, "Cannot connect to the Docker daemon");

        var report = (await Sut(r).ReportAsync("p1", default))!;

        Assert.IsNotNull(report.Error);
        StringAssert.Contains(report.Error, "Docker");
    }

    [TestMethod]
    public async Task Report_ForAnUnknownProject_IsNull()
    {
        Assert.IsNull(await Sut(new Scripted()).ReportAsync("nope", default));
    }

    [TestMethod]
    public async Task Report_NeverBuildsAShellPathFromAnUnsafeProjectId()
    {
        var r = new Scripted();
        var evil = new ProjectConfig { Id = "../../etc", Services = [new ServiceConfig { Name = "A", DockerImageName = "a" }] };

        var report = await new ProjectSpaceService(r, new Projects(evil), new Builds()).ReportAsync("../../etc", default);

        Assert.IsFalse(r.Calls.Any(c => c.Contains("du ")), "no filesystem command may use the unsafe id");
        Assert.IsNotNull(report);
    }

    // ── the one-click clean ───────────────────────────────────────────

    [TestMethod]
    public async Task Clean_RemovesUnusedImagesOfThisProjectOnly_NeverForced()
    {
        var r = new Scripted()
            .When("images", string.Join("\n", Img("shop/api", "1.0.1", "500MB"), Img("shop/api", "1.0.2", "400MB"), Img("redis", "7", "100MB")))
            .When("ps", "shop/api:1.0.1\n");

        var result = await Sut(r).CleanAsync("p1", default);

        var removed = r.Calls.Where(c => c.Contains(" rmi ")).ToList();
        Assert.AreEqual(1, removed.Count);
        StringAssert.EndsWith(removed[0], "shop/api:1.0.2");
        Assert.IsFalse(removed.Any(c => c.Contains("-f")), "never force");
        Assert.IsTrue(result.Items.Single().Removed);
    }

    [TestMethod]
    public async Task Clean_RemovesLeftoverBuildCopies_ButNotTheOneARunningBuildUses()
    {
        var r = new Scripted()
            .When("build/p1", Du((2048, BuildDir("old1")), (1024, BuildDir("live"))))
            .When("images", "");

        await Sut(r, null, Build("live", BuildStatus.Running, "1.0.5")).CleanAsync("p1", default);

        Assert.IsTrue(r.Calls.Any(c => c.Contains("worktree remove") && c.Contains(BuildDir("old1"))));
        Assert.IsFalse(r.Calls.Any(c => c.Contains("worktree remove") && c.Contains(BuildDir("live"))));
    }

    [TestMethod]
    public async Task Clean_ReportsHowMuchWasFreed_ByMeasuringAgain()
    {
        var r = new Scripted().When("images", Img("shop/api", "1.0.2", "400MB"));
        var svc = Sut(r);
        // After the removal the image is gone.
        r.When("images", "");
        var before = new Scripted().When("images", Img("shop/api", "1.0.2", "400MB"));

        var result = await new ProjectSpaceService(before, new Projects(Shop()), new Builds()).CleanAsync("p1", default);

        Assert.IsTrue(result.Items.Single().Removed);
        Assert.IsTrue(result.FreedBytes >= 0);
        Assert.IsNotNull(result.Report);
    }

    [TestMethod]
    public async Task Clean_ForAnUnknownProject_DoesNothing()
    {
        var r = new Scripted();

        var result = await Sut(r).CleanAsync("nope", default);

        Assert.AreEqual(0, result.Items.Count);
        Assert.IsFalse(r.Calls.Any(c => c.Contains("rmi")));
    }
}
