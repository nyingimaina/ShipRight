using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.WatchBranch;

namespace ShipRight.Tests.Modules.WatchBranch;

// Contract integration tests — the behaviours of WatchBranchHistoryStore
// (watch-branch/history.json) must hold for SqliteWatchBranchHistoryStore.
[TestClass]
public class SqliteWatchBranchHistoryStoreTests : IDisposable
{
    private readonly string _tmpDir;

    public SqliteWatchBranchHistoryStoreTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"sr_sqlite_watch_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        // Release pooled SQLite connections before deleting the temp directory.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_tmpDir, recursive: true);
    }

    private static WatchBranchHistoryRecord MakeRecord(string projectId, string status = "triggered",
        DateTime? triggeredAt = null) => new()
    {
        ProjectId = projectId,
        ProjectName = $"Project-{projectId}",
        BranchName = "main",
        Status = status,
        TriggeredAt = triggeredAt ?? DateTime.UtcNow,
    };

    [TestMethod]
    public void Append_And_Query_ReturnsRecord()
    {
        var store = new SqliteWatchBranchHistoryStore(_tmpDir);
        var record = MakeRecord("proj-1");

        store.Append(record);

        var results = store.Query();
        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("proj-1", results[0].ProjectId);
        Assert.AreEqual("Project-proj-1", results[0].ProjectName);
    }

    [TestMethod]
    public void Append_SurvivesStoreRestart()
    {
        var store1 = new SqliteWatchBranchHistoryStore(_tmpDir);
        store1.Append(MakeRecord("alpha"));

        var store2 = new SqliteWatchBranchHistoryStore(_tmpDir);
        var results = store2.Query();

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("alpha", results[0].ProjectId);
    }

    [TestMethod]
    public void Query_FiltersByProjectId()
    {
        var store = new SqliteWatchBranchHistoryStore(_tmpDir);
        store.Append(MakeRecord("proj-a"));
        store.Append(MakeRecord("proj-b", triggeredAt: DateTime.UtcNow.AddMinutes(1)));

        var a = store.Query(projectId: "proj-a");
        Assert.AreEqual(1, a.Count);
        Assert.AreEqual("proj-a", a[0].ProjectId);

        var b = store.Query(projectId: "proj-b");
        Assert.AreEqual(1, b.Count);
        Assert.AreEqual("proj-b", b[0].ProjectId);
    }

    [TestMethod]
    public void Query_FiltersByStatus()
    {
        var store = new SqliteWatchBranchHistoryStore(_tmpDir);
        store.Append(MakeRecord("p1"));
        store.Append(MakeRecord("p2", status: "failed", triggeredAt: DateTime.UtcNow.AddMinutes(1)));

        var triggered = store.Query(status: "triggered");
        Assert.AreEqual(1, triggered.Count);
        Assert.AreEqual("p1", triggered[0].ProjectId);

        var failed = store.Query(status: "failed");
        Assert.AreEqual(1, failed.Count);
        Assert.AreEqual("p2", failed[0].ProjectId);
    }

    [TestMethod]
    public void Query_RespectsLimit_AndOrdersNewestFirst()
    {
        var store = new SqliteWatchBranchHistoryStore(_tmpDir);
        for (var i = 0; i < 10; i++)
            store.Append(MakeRecord($"p{i:D2}", triggeredAt: DateTime.UtcNow.AddMinutes(i)));

        var limited = store.Query(limit: 3);
        Assert.AreEqual(3, limited.Count);
        Assert.AreEqual("p09", limited[0].ProjectId, "Newest record must come first");
    }

    [TestMethod]
    public void Query_ReturnsEmpty_WhenNoRecords()
    {
        var store = new SqliteWatchBranchHistoryStore(_tmpDir);
        var results = store.Query();
        Assert.AreEqual(0, results.Count);
    }

    [TestMethod]
    public void Append_MultipleRecords_AllPersisted()
    {
        var store1 = new SqliteWatchBranchHistoryStore(_tmpDir);
        store1.Append(MakeRecord("p1"));
        store1.Append(MakeRecord("p2"));
        store1.Append(MakeRecord("p3"));

        var store2 = new SqliteWatchBranchHistoryStore(_tmpDir);
        var results = store2.Query();
        Assert.AreEqual(3, results.Count);
    }

    // ── Migration integration tests ──────────────────────────────────────────

    [TestMethod]
    public void MigratesFromJsonOnFirstRun_RecordsReadableFromSqlite()
    {
        var watchDir = Path.Combine(_tmpDir, "watch-branch");
        Directory.CreateDirectory(watchDir);
        var legacy = new List<WatchBranchHistoryRecord>
        {
            MakeRecord("json-1", triggeredAt: DateTime.UtcNow.AddMinutes(-2)),
            MakeRecord("json-2", status: "skipped", triggeredAt: DateTime.UtcNow.AddMinutes(-1)),
        };
        var jsonPath = Path.Combine(watchDir, "history.json");
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(legacy,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

        var store = new SqliteWatchBranchHistoryStore(_tmpDir);

        var all = store.Query();
        Assert.AreEqual(2, all.Count, "Both records must be migrated");
        Assert.IsTrue(all.Any(r => r.ProjectId == "json-1"));
        Assert.IsTrue(all.Any(r => r.ProjectId == "json-2" && r.Status == "skipped"));

        Assert.IsFalse(File.Exists(jsonPath), "history.json should be removed after migration");
        Assert.IsTrue(File.Exists(jsonPath + ".migrated"), "history.json.migrated should exist");

        var store2 = new SqliteWatchBranchHistoryStore(_tmpDir);
        Assert.AreEqual(2, store2.Query().Count, "Data must survive restart after migration");
    }

    [TestMethod]
    public void SecondOpenAfterMigration_DoesNotDuplicateRecords()
    {
        var watchDir = Path.Combine(_tmpDir, "watch-branch");
        Directory.CreateDirectory(watchDir);
        File.WriteAllText(Path.Combine(watchDir, "history.json"),
            JsonSerializer.Serialize(new[] { MakeRecord("dup-1") },
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        _ = new SqliteWatchBranchHistoryStore(_tmpDir);

        var store2 = new SqliteWatchBranchHistoryStore(_tmpDir);
        Assert.AreEqual(1, store2.Query().Count, "Must not duplicate on second open");
    }
}
