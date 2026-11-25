using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Scheduler;
using ShipRight.Shared.Tenancy;

namespace ShipRight.Tests.Modules.Scheduler;

[TestClass]
public class JsonBackupScheduleStoreTests
{
    private string _dir = string.Empty;

    private sealed class Context(Guid? company, bool system = false) : ICompanyContext
    {
        public Guid? CompanyId => company;
        public bool IsSystem => system;
        public Guid RequireCompanyId() => company ?? Guid.Empty;
        public IDisposable BeginSystemScope() => new Scope();
        private sealed class Scope : IDisposable { public void Dispose() { } }
    }

    [TestInitialize]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sr-schedules-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private static BackupScheduleDefinition Schedule(string projectId = "p1") => new()
    {
        ProjectId = projectId,
        ProjectName = "Shop",
        DatabaseName = "shop",
        Cron = "0 2 * * *",
        TimeZone = "Africa/Nairobi",
        RuntimeScheduleId = Guid.NewGuid(),
    };

    [TestMethod]
    public async Task SavedSchedules_SurviveANewStoreInstance()
    {
        var first = new JsonBackupScheduleStore(_dir, new Context(Guid.NewGuid()));
        var schedule = Schedule();
        await first.SaveAsync(schedule);

        var second = new JsonBackupScheduleStore(_dir, null);
        var all = await second.GetAllAsync();

        // No company context: the desktop store keeps everything together.
        Assert.AreEqual(1, all.Count);
        Assert.AreEqual("0 2 * * *", all[0].Cron);
        Assert.AreEqual(schedule.RuntimeScheduleId, all[0].RuntimeScheduleId);
    }

    [TestMethod]
    public async Task AnotherCompany_CannotSeeOrDeleteTheSchedule()
    {
        var ownerCompany = Guid.NewGuid();
        var owner = new JsonBackupScheduleStore(_dir, new Context(ownerCompany));
        var schedule = Schedule();
        await owner.SaveAsync(schedule);

        var other = new JsonBackupScheduleStore(_dir, new Context(Guid.NewGuid()));
        Assert.AreEqual(0, (await other.GetAllAsync()).Count);
        Assert.IsNull(await other.GetByRuntimeIdAsync(schedule.RuntimeScheduleId!.Value));

        await other.DeleteByRuntimeIdAsync(schedule.RuntimeScheduleId.Value);
        Assert.AreEqual(1, (await owner.GetAllAsync()).Count);
    }

    [TestMethod]
    public async Task DeleteByRuntimeId_RemovesOnlyThatRegistration()
    {
        var store = new JsonBackupScheduleStore(_dir, new Context(Guid.NewGuid()));
        var kept = Schedule("p1");
        var removed = Schedule("p2");
        await store.SaveAsync(kept);
        await store.SaveAsync(removed);

        await store.DeleteByRuntimeIdAsync(removed.RuntimeScheduleId!.Value);

        var all = await store.GetAllAsync();
        Assert.AreEqual(1, all.Count);
        Assert.AreEqual("p1", all[0].ProjectId);
    }

    [TestMethod]
    public async Task SystemScope_SeesEveryCompany()
    {
        var storeA = new JsonBackupScheduleStore(_dir, new Context(Guid.NewGuid()));
        var storeB = new JsonBackupScheduleStore(_dir, new Context(Guid.NewGuid()));
        await storeA.SaveAsync(Schedule("p1"));
        await storeB.SaveAsync(Schedule("p2"));

        var system = new JsonBackupScheduleStore(_dir, new Context(null, system: true));
        Assert.AreEqual(2, (await system.GetAllAsync()).Count);
    }
}
