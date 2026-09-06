using System.Text.Json;
using Jattac.Libs.Tempo;
using Jattac.Libs.Tempo.Scheduling;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Scheduler;

namespace ShipRight.Tests.Modules.Scheduler;

// Contract integration tests — the behaviours of BackupOverflowStore
// (scheduler/overflow/*.json) must hold for SqliteBackupOverflowStore.
[TestClass]
public class SqliteBackupOverflowStoreTests : IDisposable
{
    private readonly string _tmpDir;

    public SqliteBackupOverflowStoreTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"sr_sqlite_overflow_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        // Release pooled SQLite connections before deleting the temp directory.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_tmpDir, recursive: true);
    }

    private static BackupJob MakeJob(string projectId) => new()
    {
        TenantId = BackupJob.TenantIdFromProject(projectId),
        ProjectId = projectId,
        ProjectName = $"Project-{projectId}",
        DatabaseName = "test-db",
    };

    private static TempoScheduler<BackupJob> CreateScheduler() => new(
        new TempoQueue<TempoScheduledWork<BackupJob>>(
            new RecordingProcessor(),
            new TempoQueueSettings
            {
                MessagesPerSecond = 3,
                BurstCapacity = 6,
                ChannelCapacity = 500,
            },
            null),
        new TempoSchedulerSettings
        {
            TickInterval = TimeSpan.FromSeconds(5),
            MaxCatchUpSlots = 10,
        });

    [TestMethod]
    public void Save_And_List_ReturnsRecord()
    {
        var store = new SqliteBackupOverflowStore(_tmpDir, CreateScheduler());
        store.Save(MakeJob("proj-1"), "disk full");

        var records = store.List();
        Assert.AreEqual(1, records.Count);
        Assert.AreEqual("proj-1", records[0].Job.ProjectId);
        Assert.AreEqual("disk full", records[0].ErrorMessage);
    }

    [TestMethod]
    public void Save_SurvivesStoreRestart()
    {
        var store1 = new SqliteBackupOverflowStore(_tmpDir, CreateScheduler());
        store1.Save(MakeJob("alpha"), "boom");

        var store2 = new SqliteBackupOverflowStore(_tmpDir, CreateScheduler());
        var records = store2.List();

        Assert.AreEqual(1, records.Count);
        Assert.AreEqual("alpha", records[0].Job.ProjectId);
    }

    [TestMethod]
    public void List_FiltersByProjectId()
    {
        var store = new SqliteBackupOverflowStore(_tmpDir, CreateScheduler());
        store.Save(MakeJob("proj-a"), "err a");
        store.Save(MakeJob("proj-b"), "err b");

        var a = store.List("proj-a");
        Assert.AreEqual(1, a.Count);
        Assert.AreEqual("proj-a", a[0].Job.ProjectId);

        var b = store.List("proj-b");
        Assert.AreEqual(1, b.Count);
        Assert.AreEqual("proj-b", b[0].Job.ProjectId);
    }

    [TestMethod]
    public void List_ReturnsEmpty_WhenNoRecords()
    {
        var store = new SqliteBackupOverflowStore(_tmpDir, CreateScheduler());
        var records = store.List();
        Assert.AreEqual(0, records.Count);
    }

    [TestMethod]
    public void Count_ReflectsSavedRecords_AndFiltersByProjectId()
    {
        var store = new SqliteBackupOverflowStore(_tmpDir, CreateScheduler());
        store.Save(MakeJob("proj-a"), "err a");
        store.Save(MakeJob("proj-a"), "err a2");
        store.Save(MakeJob("proj-b"), "err b");

        Assert.AreEqual(3, store.Count());
        Assert.AreEqual(2, store.Count("proj-a"));
        Assert.AreEqual(1, store.Count("proj-b"));
    }

    [TestMethod]
    public async Task ReplayAsync_ReRegistersJobsAndClearsRecords()
    {
        var scheduler = CreateScheduler();
        var store = new SqliteBackupOverflowStore(_tmpDir, scheduler);
        store.Save(MakeJob("proj-1"), "boom");
        store.Save(MakeJob("proj-2"), "bang");

        await store.ReplayAsync();

        Assert.AreEqual(0, store.Count(), "Records must be cleared after replay");
    }

    [TestMethod]
    public async Task ReplayAsync_WithProjectId_OnlyClearsThatProject()
    {
        var store = new SqliteBackupOverflowStore(_tmpDir, CreateScheduler());
        store.Save(MakeJob("proj-a"), "err a");
        store.Save(MakeJob("proj-b"), "err b");

        await store.ReplayAsync("proj-a");

        Assert.AreEqual(0, store.Count("proj-a"));
        Assert.AreEqual(1, store.Count("proj-b"));
    }

    // ── Migration integration tests ──────────────────────────────────────────

    [TestMethod]
    public void MigratesFromJsonOnFirstRun_RecordsReadableFromSqlite()
    {
        var overflowDir = Path.Combine(_tmpDir, "scheduler", "overflow");
        Directory.CreateDirectory(overflowDir);
        var legacy = new OverflowRecord
        {
            Job = MakeJob("json-p"),
            FailedAt = DateTime.UtcNow.AddMinutes(-5),
            ErrorMessage = "old failure",
        };
        File.WriteAllText(Path.Combine(overflowDir, "json-p_2026-01-01_00-00-00_abcdef012345.json"),
            JsonSerializer.Serialize(legacy,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

        var store = new SqliteBackupOverflowStore(_tmpDir, CreateScheduler());

        var all = store.List();
        Assert.AreEqual(1, all.Count, "Record must be migrated");
        Assert.AreEqual("json-p", all[0].Job.ProjectId);
        Assert.AreEqual("old failure", all[0].ErrorMessage);
        Assert.AreEqual("Project-json-p", all[0].Job.ProjectName);

        Assert.IsFalse(Directory.Exists(overflowDir), "overflow dir should be removed after migration");
        Assert.IsTrue(Directory.Exists(overflowDir + ".migrated"), "overflow.migrated should exist");

        var store2 = new SqliteBackupOverflowStore(_tmpDir, CreateScheduler());
        Assert.AreEqual(1, store2.List().Count, "Data must survive restart after migration");
    }

    private sealed class RecordingProcessor : ITempoProcessor<TempoScheduledWork<BackupJob>>
    {
        public List<BackupJob> Jobs { get; } = new();

        public Task<bool> ProcessAsync(TempoScheduledWork<BackupJob> work, CancellationToken ct)
        {
            Jobs.Add(work.Job);
            return Task.FromResult(true);
        }
    }
}
