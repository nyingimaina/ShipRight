using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Builds;
using ShipRight.Modules.Projects;
using ShipRight.Modules.System.DockerCleanup;
using ShipRight.Modules.System.WslDisk;
using ShipRight.Shared.ProcessRunner;

namespace ShipRight.Tests.Modules.DockerCleanup;

[TestClass]
public class DiskCleanupJobTests
{
    private const long Gb = 1024L * 1024 * 1024;

    private sealed class Docker : IProcessRunner
    {
        public List<string> Calls { get; } = [];
        public string Images = "shop/api\t1\tsha256:a\t500MB\t2 days ago";
        public Func<string, int>? ExitFor;

        public Task<ProcessResult> RunAsync(string executable, string[] args, string? workingDir,
            Func<string, Task>? onOutput = null, Func<string, Task>? onError = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? envOverride = null, TimeSpan? timeout = null, string? stdin = null)
        {
            var line = string.Join(" ", args);
            Calls.Add(line);
            var exit = ExitFor?.Invoke(line) ?? 0;
            var stdout = args.Contains("images") ? Images : args.Contains("df") ? "Images 1 0 500MB 500MB (100%)" : "Total reclaimed space: 1MB";
            return Task.FromResult(new ProcessResult(exit, exit == 0 ? stdout : "", exit == 0 ? "" : "boom", TimeSpan.Zero));
        }
    }

    private sealed class Store(params BuildRecord[] records) : IProjectStore
    {
        public int Count => 0;
        public Task<List<ProjectConfig>> GetAllAsync() => Task.FromResult(new List<ProjectConfig> { new() { Services = [new ServiceConfig { Name = "Api", DockerImageName = "shop/api" }] } });
        public Task<ProjectConfig?> GetByIdAsync(string id) => Task.FromResult<ProjectConfig?>(null);
        public Task<ProjectConfig?> GetByNameAsync(string name) => Task.FromResult<ProjectConfig?>(null);
        public Task SaveAsync(ProjectConfig project) => Task.CompletedTask;
        public Task DeleteAsync(string id) => Task.CompletedTask;
    }

    private sealed class Builds(params BuildRecord[] active) : IBuildStore
    {
        public int Count => active.Length;
        public Task SaveAsync(BuildRecord record) => Task.CompletedTask;
        public Task<BuildRecord?> GetByIdAsync(string id) => Task.FromResult(active.FirstOrDefault(r => r.Id == id));
        public Task<List<BuildRecord>> QueryAsync(string? projectId, string? status, DateTime? from, DateTime? to,
            string? gitTag, int page, int pageSize) =>
            Task.FromResult(active.Where(r => status is null || r.Status.ToString() == status).ToList());
        public Task<int> CountQueryAsync(string? projectId, string? status, DateTime? from, DateTime? to, string? gitTag) => Task.FromResult(active.Length);
        public Task MarkInterruptedAsync() => Task.CompletedTask;
    }

    private static WslCompactResult Shrunk(long before, long after) =>
        new(true, null, [new WslCompactFileResult("C:\\x\\ext4.vhdx", before, after)], ["done"]);

    private static (DiskCleanupJobService Svc, Docker Docker, List<int> ShrinkCalls) Build(
        BuildRecord[]? activeBuilds = null, Func<Task<WslCompactResult>>? shrink = null, long diskBytes = 100 * Gb)
    {
        var docker = new Docker();
        var shrinkCalls = new List<int>();
        var svc = new DiskCleanupJobService(
            new DockerCleanupService(docker, new Store()),
            new Builds(activeBuilds ?? []),
            (_, _) => { shrinkCalls.Add(1); return (shrink ?? (() => Task.FromResult(Shrunk(100 * Gb, 30 * Gb))))(); },
            _ => Task.FromResult(diskBytes));
        return (svc, docker, shrinkCalls);
    }

    private static BuildRecord Active(BuildStatus status, string project = "Shop") =>
        new() { Id = Guid.NewGuid().ToString(), ProjectName = project, Status = status, CurrentStepName = "DockerBuild" };

    private static readonly DiskJobRequest Everything = new(["shop/api:1"], true, true, true);

    // ── blockers ──────────────────────────────────────────────────────

    [TestMethod]
    public async Task ActiveBuilds_ListsRunningPausedAndPending_WithWhatTheUserNeedsToActOnThem()
    {
        var paused = Active(BuildStatus.Paused, "Shop");
        var running = Active(BuildStatus.Running, "Blog");

        var found = await ActiveBuilds.ListAsync(new Builds(paused, running, new BuildRecord { Status = BuildStatus.Deployed }));

        Assert.AreEqual(2, found.Count);
        var p = found.Single(b => b.Id == paused.Id);
        Assert.AreEqual("Shop", p.ProjectName);
        Assert.AreEqual("Paused", p.Status);
        Assert.IsTrue(p.CanAbort);
    }

    [TestMethod]
    public async Task ActiveBuilds_APendingBuildCannotBeAborted_ButIsStillListed()
    {
        var found = await ActiveBuilds.ListAsync(new Builds(Active(BuildStatus.Pending)));

        Assert.AreEqual(1, found.Count);
        Assert.IsFalse(found[0].CanAbort);
    }

    // ── the job ───────────────────────────────────────────────────────

    [TestMethod]
    public async Task ShrinkRequestedWhileABuildIsActive_StopsBeforeDeletingAnything_AndNamesTheBuild()
    {
        var (svc, docker, shrinkCalls) = Build([Active(BuildStatus.Paused)]);

        var job = await svc.StartAndWaitAsync(Everything);

        Assert.AreEqual(DiskJobStatus.Blocked, job.Status);
        Assert.AreEqual(1, job.Blockers.Count);
        Assert.IsFalse(docker.Calls.Any(c => c.Contains("rmi") || c.Contains("prune")), "nothing may be deleted when the job cannot finish");
        Assert.AreEqual(0, shrinkCalls.Count);
    }

    [TestMethod]
    public async Task ShrinkRequestedButNoDiskFileExists_StopsBeforeDeletingAnything_AndSaysWhereItLooked()
    {
        var docker = new Docker();
        var svc = new DiskCleanupJobService(
            new DockerCleanupService(docker, new Store()), new Builds(),
            (_, _) => Task.FromResult(Shrunk(1, 1)), _ => Task.FromResult(0L),
            () => [@"D:\wsl\ubuntu", @"C:\Users\x\AppData\Local\wsl"]);

        var job = await svc.StartAndWaitAsync(Everything);

        Assert.AreEqual(DiskJobStatus.Failed, job.Status);
        StringAssert.Contains(job.Error, "could not find the WSL disk file");
        StringAssert.Contains(job.Error, @"D:\wsl\ubuntu");
        StringAssert.Contains(job.Error, "Nothing was");
        Assert.IsFalse(docker.Calls.Any(c => c.Contains("rmi") || c.Contains("prune")), "nothing may be deleted when the job cannot finish");
        Assert.IsTrue(job.Steps.All(s => s.Status == DiskStepStatus.Skipped));
    }

    [TestMethod]
    public async Task AJobWithoutAShrink_DoesNotNeedADiskFile()
    {
        var svc = new DiskCleanupJobService(
            new DockerCleanupService(new Docker(), new Store()), new Builds(),
            (_, _) => Task.FromResult(Shrunk(1, 1)), _ => Task.FromResult(0L));

        var job = await svc.StartAndWaitAsync(new DiskJobRequest([], false, true, false));

        Assert.AreEqual(DiskJobStatus.Succeeded, job.Status);
    }

    [TestMethod]
    public async Task IfTheDiskSizeCannotBeRead_TheJobStillTriesTheShrink()
    {
        var shrinkCalls = 0;
        var svc = new DiskCleanupJobService(
            new DockerCleanupService(new Docker(), new Store()), new Builds(),
            (_, _) => { shrinkCalls++; return Task.FromResult(Shrunk(100 * Gb, 30 * Gb)); },
            _ => throw new InvalidOperationException("wsl is slow"));

        var job = await svc.StartAndWaitAsync(Everything);

        Assert.AreEqual(DiskJobStatus.Succeeded, job.Status);
        Assert.AreEqual(1, shrinkCalls);
    }

    [TestMethod]
    public async Task AJobWithoutAShrink_IgnoresRunningBuilds()
    {
        var (svc, _, _) = Build([Active(BuildStatus.Running)]);

        var job = await svc.StartAndWaitAsync(new DiskJobRequest(["shop/api:1"], true, true, false));

        Assert.AreEqual(DiskJobStatus.Succeeded, job.Status);
    }

    [TestMethod]
    public async Task OnlyTheRequestedStepsAreListed_InOrder()
    {
        var (svc, _, _) = Build();

        var job = await svc.StartAndWaitAsync(new DiskJobRequest([], false, true, false));
        CollectionAssert.AreEqual(new[] { "cache" }, job.Steps.Select(s => s.Id).ToArray());

        var full = await Build().Svc.StartAndWaitAsync(Everything);
        CollectionAssert.AreEqual(new[] { "images", "leftovers", "cache", "shrink" }, full.Steps.Select(s => s.Id).ToArray());
    }

    [TestMethod]
    public async Task EveryStepFinishes_WithTimesAndAnEstimate_AndTheShrinkResultIsKept()
    {
        var (svc, _, shrinkCalls) = Build();

        var job = await svc.StartAndWaitAsync(Everything);

        Assert.AreEqual(DiskJobStatus.Succeeded, job.Status);
        Assert.IsTrue(job.Steps.All(s => s.Status == DiskStepStatus.Done));
        Assert.IsTrue(job.Steps.All(s => s.StartedAt is not null && s.FinishedAt is not null && s.EstimatedSeconds > 0));
        Assert.AreEqual(1, shrinkCalls.Count);
        Assert.AreEqual(30 * Gb, job.Shrink!.Files.Single().AfterBytes);
        Assert.IsNotNull(job.Cleanup);
        Assert.IsNotNull(job.FinishedAt);
    }

    [TestMethod]
    public async Task TheShrinkEstimate_GrowsWithTheSizeOfTheDiskFile()
    {
        var small = (await Build(diskBytes: 10 * Gb).Svc.StartAndWaitAsync(Everything)).Steps.Single(s => s.Id == "shrink").EstimatedSeconds;
        var big = (await Build(diskBytes: 200 * Gb).Svc.StartAndWaitAsync(Everything)).Steps.Single(s => s.Id == "shrink").EstimatedSeconds;

        Assert.IsTrue(big > small * 5, $"{small} vs {big}");
    }

    [TestMethod]
    public async Task AFailedStep_FailsTheJob_AndSkipsTheRest()
    {
        var (svc, docker, shrinkCalls) = Build();
        docker.ExitFor = line => line.Contains("rmi") ? 1 : 0;

        var job = await svc.StartAndWaitAsync(Everything);

        // a refused removal is reported per image, it does not abort the job
        Assert.AreEqual(DiskJobStatus.Succeeded, job.Status);
        Assert.IsFalse(job.Cleanup!.Items.Single().Removed);

        docker.ExitFor = line => line.Contains("builder") ? 1 : 0;
        var failed = await svc.StartAndWaitAsync(Everything);
        Assert.AreEqual(DiskStepStatus.Done, failed.Steps.Single(s => s.Id == "images").Status);
        Assert.AreEqual(DiskStepStatus.Failed, failed.Steps.Single(s => s.Id == "cache").Status);
        Assert.AreEqual(DiskStepStatus.Skipped, failed.Steps.Single(s => s.Id == "shrink").Status);
        Assert.AreEqual(DiskJobStatus.Failed, failed.Status);
        Assert.AreEqual(1, shrinkCalls.Count, "only the first job shrank");
    }

    [TestMethod]
    public async Task AShrinkThatIsRefused_FailsTheJob_ButKeepsWhatWasCleaned()
    {
        var (svc, _, _) = Build(shrink: () => Task.FromResult(new WslCompactResult(false, "diskpart needs administrator permission", [], ["Answer Yes."])));

        var job = await svc.StartAndWaitAsync(Everything);

        Assert.AreEqual(DiskJobStatus.Failed, job.Status);
        StringAssert.Contains(job.Error, "administrator permission");
        Assert.IsNotNull(job.Cleanup);
        Assert.AreEqual(DiskStepStatus.Failed, job.Steps.Single(s => s.Id == "shrink").Status);
    }

    [TestMethod]
    public async Task AShrinkThatThrows_FailsTheJobInsteadOfLosingIt()
    {
        var (svc, _, _) = Build(shrink: () => throw new InvalidOperationException("kaboom"));

        var job = await svc.StartAndWaitAsync(Everything);

        Assert.AreEqual(DiskJobStatus.Failed, job.Status);
        StringAssert.Contains(job.Error, "kaboom");
    }

    [TestMethod]
    public async Task StartingWhileOneIsRunning_ReturnsTheRunningOne_AndDoesNotStartASecond()
    {
        var gate = new TaskCompletionSource<WslCompactResult>();
        var (svc, _, shrinkCalls) = Build(shrink: () => gate.Task);

        var first = svc.Start(Everything);
        await svc.WaitForStepAsync(first.Id, "shrink");
        var second = svc.Start(Everything);

        Assert.AreEqual(first.Id, second.Id);
        gate.SetResult(Shrunk(100 * Gb, 30 * Gb));
        await svc.WaitAsync(first.Id);
        Assert.AreEqual(1, shrinkCalls.Count);
    }

    [TestMethod]
    public async Task Latest_ReturnsTheMostRecentJob_SoARefreshedPageCanPickItBackUp()
    {
        var (svc, _, _) = Build();
        Assert.IsNull(svc.Latest());

        var job = await svc.StartAndWaitAsync(new DiskJobRequest([], false, true, false));

        Assert.AreEqual(job.Id, svc.Latest()!.Id);
        Assert.AreEqual(job.Id, svc.Get(job.Id)!.Id);
        Assert.IsNull(svc.Get("nope"));
    }
}
