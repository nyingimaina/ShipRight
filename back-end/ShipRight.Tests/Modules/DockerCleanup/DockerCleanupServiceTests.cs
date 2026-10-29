using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Projects;
using ShipRight.Modules.System.DockerCleanup;
using ShipRight.Shared.ProcessRunner;

namespace ShipRight.Tests.Modules.DockerCleanup;

[TestClass]
public class DockerCleanupServiceTests
{
    private const string Sep = "\t";

    private sealed class ScriptedRunner : IProcessRunner
    {
        public string Images = "";
        public string InUse = "";
        public int ImagesExit;
        public readonly HashSet<string> RmiFails = [];
        public List<string[]> Calls { get; } = [];

        public Task<ProcessResult> RunAsync(string executable, string[] args, string? workingDir,
            Func<string, Task>? onOutput = null, Func<string, Task>? onError = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? envOverride = null, TimeSpan? timeout = null, string? stdin = null)
        {
            Calls.Add(args);
            ProcessResult R(int exit, string o = "", string e = "") => new(exit, o, e, TimeSpan.Zero);
            if (args.Contains("images")) return Task.FromResult(ImagesExit == 0 ? R(0, Images) : R(1, "", "Cannot connect to the Docker daemon"));
            if (args.Contains("ps")) return Task.FromResult(R(0, InUse));
            if (args.Contains("rmi")) return Task.FromResult(RmiFails.Contains(args.Last()) ? R(1, "", "conflict") : R(0, $"Untagged: {args.Last()}"));
            if (args.Contains("builder")) return Task.FromResult(R(0, "Total:\t2.1GB"));
            if (args.Contains("prune")) return Task.FromResult(R(0, "Total reclaimed space: 40MB"));
            if (args.Contains("df")) return Task.FromResult(R(0, "TYPE   TOTAL  ACTIVE  SIZE  RECLAIMABLE\nImages  5  1  3.2GB  2.9GB (90%)"));
            return Task.FromResult(R(0));
        }

        public IEnumerable<string> Removed => Calls.Where(c => c.Contains("rmi")).Select(c => c.Last());
    }

    private sealed class Store(params ProjectConfig[] projects) : IProjectStore
    {
        public int Count => projects.Length;
        public Task<List<ProjectConfig>> GetAllAsync() => Task.FromResult(projects.ToList());
        public Task<ProjectConfig?> GetByIdAsync(string id) => Task.FromResult(projects.FirstOrDefault(p => p.Id == id));
        public Task<ProjectConfig?> GetByNameAsync(string name) => Task.FromResult(projects.FirstOrDefault(p => p.Name == name));
        public Task SaveAsync(ProjectConfig project) => Task.CompletedTask;
        public Task DeleteAsync(string id) => Task.CompletedTask;
    }

    private static ProjectConfig Shop() => new()
    {
        Id = "p1",
        Services = [new ServiceConfig { Name = "Api", DockerImageName = "shop/api" }, new ServiceConfig { Name = "Web", DockerImageName = "ghcr.io/acme/web" }],
    };

    private static string Line(string repo, string tag, string id, string size, string age = "2 days ago") =>
        string.Join(Sep, repo, tag, id, size, age);

    private static DockerCleanupService Sut(ScriptedRunner r) => new(r, new Store(Shop()));

    [TestMethod]
    public async Task Preview_SplitsImagesIntoShipRightsAndOthers_OnlyShipRightsPreselected()
    {
        var r = new ScriptedRunner
        {
            Images = string.Join("\n", Line("shop/api", "1.0.0", "sha256:a", "500MB"), Line("ghcr.io/acme/web", "2.0.0", "sha256:b", "1.2GB"),
                Line("postgres", "16", "sha256:c", "400MB")),
        };

        var p = await Sut(r).PreviewAsync(default);

        Assert.AreEqual(3, p.Images.Count);
        Assert.IsTrue(p.Images.Where(i => i.Owner == "ShipRight").All(i => i.SelectedByDefault));
        Assert.AreEqual(2, p.Images.Count(i => i.Owner == "ShipRight"));
        var other = p.Images.Single(i => i.Reference == "postgres:16");
        Assert.AreEqual("Other", other.Owner);
        Assert.IsFalse(other.SelectedByDefault);
    }

    [TestMethod]
    public async Task Preview_LeavesOutImagesAContainerUses_AndCountsThem()
    {
        var r = new ScriptedRunner
        {
            Images = string.Join("\n", Line("shop/api", "1.0.0", "sha256:a", "500MB"), Line("shop/api", "0.9.0", "sha256:z", "490MB")),
            InUse = "shop/api:1.0.0",
        };

        var p = await Sut(r).PreviewAsync(default);

        CollectionAssert.AreEqual(new[] { "shop/api:0.9.0" }, p.Images.Select(i => i.Reference).ToArray());
        Assert.AreEqual(1, p.InUseCount);
    }

    [TestMethod]
    public async Task Preview_ATaglessContainerImage_MeansLatest()
    {
        // docker ps prints "quay.io/minio/minio" for an image that is really quay.io/minio/minio:latest.
        var r = new ScriptedRunner
        {
            Images = string.Join("\n", Line("quay.io/minio/minio", "latest", "sha256:m", "175MB"), Line("localhost:5000/app", "latest", "sha256:n", "5MB")),
            InUse = "quay.io/minio/minio",
        };

        var p = await Sut(r).PreviewAsync(default);

        CollectionAssert.AreEqual(new[] { "localhost:5000/app:latest" }, p.Images.Select(i => i.Reference).ToArray());
        Assert.AreEqual(1, p.InUseCount);
    }

    [TestMethod]
    public async Task Preview_AContainerThatNamesItsImageByShortId_KeepsThatImageOutOfTheList()
    {
        var r = new ScriptedRunner
        {
            Images = Line("shop/api", "1", "sha256:6e386bf05d58aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "5MB"),
            InUse = "6e386bf05d58",
        };

        var p = await Sut(r).PreviewAsync(default);

        Assert.AreEqual(0, p.Images.Count);
        Assert.AreEqual(1, p.InUseCount);
    }

    [TestMethod]
    public async Task Preview_CountsUntaggedImagesAsDangling_NotAsListEntries()
    {
        var r = new ScriptedRunner { Images = string.Join("\n", Line("<none>", "<none>", "sha256:d1", "80MB"), Line("shop/api", "1.0.0", "sha256:a", "5MB")) };

        var p = await Sut(r).PreviewAsync(default);

        Assert.AreEqual(1, p.DanglingCount);
        Assert.AreEqual(1, p.Images.Count);
    }

    [TestMethod]
    public async Task Preview_AddsUpTheSizeOfWhatIsPreselected()
    {
        var r = new ScriptedRunner { Images = string.Join("\n", Line("shop/api", "1", "a", "500MB"), Line("shop/api", "2", "b", "1.5GB"), Line("redis", "7", "c", "100MB")) };

        var p = await Sut(r).PreviewAsync(default);

        Assert.AreEqual(2_000_000_000L, p.SelectedBytes);
    }

    [TestMethod]
    public async Task Preview_WhenDockerIsUnreachable_SaysSoInsteadOfShowingAnEmptyList()
    {
        var r = new ScriptedRunner { ImagesExit = 1 };

        var p = await Sut(r).PreviewAsync(default);

        Assert.IsNotNull(p.Error);
        StringAssert.Contains(p.Error, "Docker");
        Assert.AreEqual(0, p.Images.Count);
    }

    [TestMethod]
    public async Task Run_RemovesOnlyWhatWasAskedFor_WithoutForce()
    {
        var r = new ScriptedRunner { Images = string.Join("\n", Line("shop/api", "1", "a", "5MB"), Line("redis", "7", "c", "5MB")) };

        var result = await Sut(r).RunAsync(new CleanupRequest(["shop/api:1"], false, false), default);

        CollectionAssert.AreEqual(new[] { "shop/api:1" }, r.Removed.ToArray());
        Assert.IsFalse(r.Calls.Any(c => c.Contains("-f") && c.Contains("rmi")), "never force-remove");
        Assert.IsTrue(result.Items.Single().Removed);
    }

    [TestMethod]
    public async Task Run_IgnoresAnythingThatIsNotAnUnusedImageRightNow()
    {
        var r = new ScriptedRunner { Images = Line("shop/api", "1", "a", "5MB"), InUse = "shop/api:1" };

        var result = await Sut(r).RunAsync(new CleanupRequest(["shop/api:1", "made/up:9"], false, false), default);

        Assert.AreEqual(0, r.Removed.Count());
        Assert.IsTrue(result.Items.All(i => !i.Removed && i.Note is not null));
    }

    [TestMethod]
    public async Task Run_ReportsAFailedRemoval_AndKeepsGoing()
    {
        var r = new ScriptedRunner { Images = string.Join("\n", Line("a", "1", "x", "5MB"), Line("b", "1", "y", "5MB")) };
        r.RmiFails.Add("a:1");

        var result = await Sut(r).RunAsync(new CleanupRequest(["a:1", "b:1"], false, false), default);

        Assert.IsFalse(result.Items.Single(i => i.Reference == "a:1").Removed);
        Assert.IsTrue(result.Items.Single(i => i.Reference == "b:1").Removed);
    }

    [TestMethod]
    public async Task Run_PrunesDanglingImagesAndBuildCache_OnlyWhenAsked()
    {
        var off = new ScriptedRunner();
        await Sut(off).RunAsync(new CleanupRequest([], false, false), default);
        Assert.IsFalse(off.Calls.Any(c => c.Contains("prune")));

        var on = new ScriptedRunner();
        var result = await Sut(on).RunAsync(new CleanupRequest([], true, true), default);
        Assert.IsTrue(on.Calls.Any(c => c.Contains("image") && c.Contains("prune")));
        Assert.IsTrue(on.Calls.Any(c => c.Contains("builder") && c.Contains("prune") && c.Contains("-af")));
        Assert.IsNotNull(result.DanglingOutput);
        Assert.IsNotNull(result.BuildCacheOutput);
    }

    [TestMethod]
    public async Task Run_NeverPrunesVolumes()
    {
        var r = new ScriptedRunner();
        await Sut(r).RunAsync(new CleanupRequest([], true, true), default);

        Assert.IsFalse(r.Calls.Any(c => c.Contains("--volumes") || c.Contains("volume")));
    }

    [TestMethod]
    public async Task Run_ReportsDiskUsageBeforeAndAfter()
    {
        var r = new ScriptedRunner();

        var result = await Sut(r).RunAsync(new CleanupRequest([], false, false), default);

        StringAssert.Contains(result.Before, "Images");
        StringAssert.Contains(result.After, "Images");
    }
}
