using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Builds;

namespace ShipRight.Tests.Modules.Builds;

[TestClass]
public class StaleBuildTests
{
    private sealed class MemoryBuilds : IBuildStore
    {
        public List<BuildRecord> Records { get; } = [];
        public int Count => Records.Count;
        public Task SaveAsync(BuildRecord record)
        {
            Records.RemoveAll(r => r.Id == record.Id);
            Records.Add(record);
            return Task.CompletedTask;
        }
        public Task<BuildRecord?> GetByIdAsync(string id) => Task.FromResult(Records.FirstOrDefault(r => r.Id == id));
        public Task<List<BuildRecord>> QueryAsync(string? projectId, string? status, DateTime? from, DateTime? to,
            string? gitTag, int page, int pageSize) =>
            Task.FromResult(Records.Where(r => status is null || r.Status.ToString() == status).ToList());
        public Task<int> CountQueryAsync(string? projectId, string? status, DateTime? from, DateTime? to, string? gitTag) => Task.FromResult(Records.Count);
        public Task MarkInterruptedAsync() => Task.CompletedTask;
    }

    private static BuildRecord Build(BuildStatus status, string id = "b1") =>
        new() { Id = id, ProjectId = "p1", ProjectName = "Shop", Status = status, CurrentStepName = "DockerBuild" };

    // ── force abort ───────────────────────────────────────────────────

    [TestMethod]
    public async Task AGhostPausedBuild_IsMarkedAborted_AndItsLeftoverFilesAreCleaned()
    {
        var store = new MemoryBuilds();
        await store.SaveAsync(Build(BuildStatus.Paused));
        BuildRecord? cleaned = null;

        var outcome = await StaleBuilds.ForceAbortAsync("b1", store, id => false, r => { cleaned = r; return Task.CompletedTask; });

        Assert.AreEqual(ForceAbortOutcome.ForceCleaned, outcome);
        var saved = (await store.GetByIdAsync("b1"))!;
        Assert.AreEqual(BuildStatus.Aborted, saved.Status);
        Assert.IsNotNull(saved.CompletedAt);
        StringAssert.Contains(saved.ErrorSummary, "nothing was running");
        Assert.AreEqual("b1", cleaned?.Id);
    }

    [TestMethod]
    public async Task AGhostRunningBuild_IsCleanedTheSameWay()
    {
        var store = new MemoryBuilds();
        await store.SaveAsync(Build(BuildStatus.Running));

        var outcome = await StaleBuilds.ForceAbortAsync("b1", store, id => false, null);

        Assert.AreEqual(ForceAbortOutcome.ForceCleaned, outcome);
    }

    [TestMethod]
    public async Task ABuildThatReallyIsRunning_IsCancelledNormally_NotForcedAndNotTouched()
    {
        var store = new MemoryBuilds();
        await store.SaveAsync(Build(BuildStatus.Running));
        var cleanupCalled = false;

        var outcome = await StaleBuilds.ForceAbortAsync("b1", store, id => true, _ => { cleanupCalled = true; return Task.CompletedTask; });

        Assert.AreEqual(ForceAbortOutcome.CancelRequested, outcome);
        Assert.AreEqual(BuildStatus.Running, (await store.GetByIdAsync("b1"))!.Status, "the real pipeline records its own end");
        Assert.IsFalse(cleanupCalled);
    }

    [TestMethod]
    [DataRow(BuildStatus.Deployed)]
    [DataRow(BuildStatus.BuildFailed)]
    [DataRow(BuildStatus.Aborted)]
    [DataRow(BuildStatus.Interrupted)]
    [DataRow(BuildStatus.Pending)]
    public async Task OnlyRunningOrPausedBuildsCanBeForced(BuildStatus status)
    {
        var store = new MemoryBuilds();
        await store.SaveAsync(Build(status));

        var outcome = await StaleBuilds.ForceAbortAsync("b1", store, id => false, null);

        Assert.AreEqual(ForceAbortOutcome.NotActive, outcome);
        Assert.AreEqual(status, (await store.GetByIdAsync("b1"))!.Status);
    }

    [TestMethod]
    public async Task AnUnknownBuild_IsNotFound()
    {
        Assert.AreEqual(ForceAbortOutcome.NotFound, await StaleBuilds.ForceAbortAsync("nope", new MemoryBuilds(), id => false, null));
    }

    [TestMethod]
    public async Task AFailingCleanup_DoesNotStopTheBuildFromBeingFreed()
    {
        var store = new MemoryBuilds();
        await store.SaveAsync(Build(BuildStatus.Paused));

        var outcome = await StaleBuilds.ForceAbortAsync("b1", store, id => false, _ => throw new InvalidOperationException("WSL is off"));

        Assert.AreEqual(ForceAbortOutcome.ForceCleaned, outcome);
        Assert.AreEqual(BuildStatus.Aborted, (await store.GetByIdAsync("b1"))!.Status);
    }

    // ── startup sweep ─────────────────────────────────────────────────

    [TestMethod]
    public async Task AtStartup_PausedAndPendingBuilds_AreMarkedInterrupted()
    {
        var store = new MemoryBuilds();
        await store.SaveAsync(Build(BuildStatus.Paused, "a"));
        await store.SaveAsync(Build(BuildStatus.Pending, "b"));
        await store.SaveAsync(Build(BuildStatus.Deployed, "c"));
        await store.SaveAsync(Build(BuildStatus.BuildFailed, "d"));

        var swept = await StaleBuilds.SweepAtStartupAsync(store);

        Assert.AreEqual(2, swept);
        Assert.AreEqual(BuildStatus.Interrupted, (await store.GetByIdAsync("a"))!.Status);
        Assert.AreEqual(BuildStatus.Interrupted, (await store.GetByIdAsync("b"))!.Status);
        Assert.AreEqual(BuildStatus.Deployed, (await store.GetByIdAsync("c"))!.Status);
        Assert.AreEqual(BuildStatus.BuildFailed, (await store.GetByIdAsync("d"))!.Status);
        StringAssert.Contains((await store.GetByIdAsync("a"))!.GetLogOutput(), "interrupted");
    }

    [TestMethod]
    public async Task TheSweep_IsHarmlessWhenThereIsNothingToDo()
    {
        Assert.AreEqual(0, await StaleBuilds.SweepAtStartupAsync(new MemoryBuilds()));
    }
}
