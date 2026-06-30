using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Scheduler;

namespace ShipRight.Tests.Modules.Scheduler;

[TestClass]
public class BackupHistoryStoreTests : IDisposable
{
    private readonly string _tmpDir;

    public BackupHistoryStoreTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"sr_hist_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose() => Directory.Delete(_tmpDir, recursive: true);

    private static BackupHistoryRecord MakeRecord(string projectId, string status = "completed",
        DateTime? startedAt = null, long durationMs = 0, long sizeBytes = 0) => new()
    {
        ProjectId = projectId,
        ProjectName = $"Project-{projectId}",
        DatabaseName = "test-db",
        Status = status,
        StartedAt = startedAt ?? DateTime.UtcNow,
        DurationMs = durationMs,
        BackupSizeBytes = sizeBytes,
    };

    [TestMethod]
    public void Append_And_Query_ReturnsRecord()
    {
        var store = new BackupHistoryStore(_tmpDir);
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
        var store1 = new BackupHistoryStore(_tmpDir);
        store1.Append(MakeRecord("alpha"));

        var store2 = new BackupHistoryStore(_tmpDir);
        var results = store2.Query();

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("alpha", results[0].ProjectId);
    }

    [TestMethod]
    public void Query_FiltersByProjectId()
    {
        var store = new BackupHistoryStore(_tmpDir);
        store.Append(MakeRecord("proj-a"));
        store.Append(MakeRecord("proj-b", startedAt: DateTime.UtcNow.AddMinutes(1)));

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
        var store = new BackupHistoryStore(_tmpDir);
        store.Append(MakeRecord("p1"));
        store.Append(MakeRecord("p2", status: "failed", startedAt: DateTime.UtcNow.AddMinutes(1)));

        var completed = store.Query(status: "completed");
        Assert.AreEqual(1, completed.Count);
        Assert.AreEqual("p1", completed[0].ProjectId);

        var failed = store.Query(status: "failed");
        Assert.AreEqual(1, failed.Count);
        Assert.AreEqual("p2", failed[0].ProjectId);
    }

    [TestMethod]
    public void Query_FiltersByDateRange()
    {
        var store = new BackupHistoryStore(_tmpDir);
        var old = DateTime.UtcNow.AddDays(-5);
        var recent = DateTime.UtcNow;
        store.Append(MakeRecord("old", startedAt: old));
        store.Append(MakeRecord("recent", startedAt: recent));

        var recentOnly = store.Query(since: DateTime.UtcNow.AddDays(-1));
        Assert.AreEqual(1, recentOnly.Count);
        Assert.AreEqual("recent", recentOnly[0].ProjectId);

        var oldOnly = store.Query(until: DateTime.UtcNow.AddDays(-1));
        Assert.AreEqual(1, oldOnly.Count);
        Assert.AreEqual("old", oldOnly[0].ProjectId);
    }

    [TestMethod]
    public void Query_RespectsLimit()
    {
        var store = new BackupHistoryStore(_tmpDir);
        for (var i = 0; i < 10; i++)
            store.Append(MakeRecord($"p{i:D2}", startedAt: DateTime.UtcNow.AddMinutes(i)));

        var limited = store.Query(limit: 3);
        Assert.AreEqual(3, limited.Count);
    }

    [TestMethod]
    public void Query_ReturnsEmpty_WhenNoRecords()
    {
        var store = new BackupHistoryStore(_tmpDir);
        var results = store.Query();
        Assert.AreEqual(0, results.Count);
    }

    [TestMethod]
    public void GetSummary_ReturnsCorrectCounts()
    {
        var store = new BackupHistoryStore(_tmpDir);
        var now = DateTime.UtcNow;
        store.Append(MakeRecord("p1", startedAt: now));
        store.Append(MakeRecord("p1", startedAt: now.AddMinutes(1)));
        store.Append(MakeRecord("p1", status: "failed", startedAt: now.AddMinutes(2)));

        var summary = store.GetSummary(since: now.AddDays(-1), until: now.AddDays(1));
        Assert.AreEqual(3, summary.TotalRuns);
        Assert.AreEqual(2, summary.SuccessfulRuns);
        Assert.AreEqual(1, summary.FailedRuns);
        Assert.AreEqual(66.7, summary.SuccessRate, 0.1);
    }

    [TestMethod]
    public void GetSummary_WhenNoRecords_Returns100PercentSuccess()
    {
        var store = new BackupHistoryStore(_tmpDir);
        var summary = store.GetSummary();
        Assert.AreEqual(0, summary.TotalRuns);
        Assert.AreEqual(100, summary.SuccessRate);
    }

    [TestMethod]
    public void GetDailyReport_ReturnsCorrectStructure()
    {
        var store = new BackupHistoryStore(_tmpDir);
        store.Append(MakeRecord("p1", startedAt: DateTime.UtcNow));

        var reports = store.GetDailyReport(days: 7);
        Assert.IsTrue(reports.Count > 0);
        var today = reports.First(r => r.Date.Date == DateTime.UtcNow.Date);
        Assert.AreEqual(1, today.TotalRuns);
        Assert.AreEqual(1, today.SuccessfulRuns);
    }

    [TestMethod]
    public void GetDailyReport_EmptyDaysHaveZeroCounts()
    {
        var store = new BackupHistoryStore(_tmpDir);
        var reports = store.GetDailyReport(days: 3);
        Assert.AreEqual(4, reports.Count);
        Assert.IsTrue(reports.All(r => r.TotalRuns == 0));
    }

    [TestMethod]
    public void GetProjectReports_GroupsByProject()
    {
        var store = new BackupHistoryStore(_tmpDir);
        store.Append(MakeRecord("alpha"));
        store.Append(MakeRecord("alpha", startedAt: DateTime.UtcNow.AddMinutes(1)));
        store.Append(MakeRecord("beta"));

        var reports = store.GetProjectReports();
        Assert.AreEqual(2, reports.Count);
        var alpha = reports.Single(r => r.ProjectId == "alpha");
        Assert.AreEqual(2, alpha.TotalRuns);
        Assert.AreEqual(2, alpha.SuccessfulRuns);
    }

    [TestMethod]
    public void Prune_RemovesOldRecords()
    {
        var store = new BackupHistoryStore(_tmpDir);
        for (var i = 0; i < 10; i++)
            store.Append(MakeRecord($"p{i:D2}", startedAt: DateTime.UtcNow.AddMinutes(i)));

        store.Prune(retainCount: 3);
        var remaining = store.Query();
        Assert.AreEqual(3, remaining.Count);
    }

    [TestMethod]
    public void Prune_DoesNotRemoveWhenUnderLimit()
    {
        var store = new BackupHistoryStore(_tmpDir);
        store.Append(MakeRecord("p1"));

        store.Prune(retainCount: 1000);
        var remaining = store.Query();
        Assert.AreEqual(1, remaining.Count);
    }

    [TestMethod]
    public void Append_MultipleRecords_AllPersisted()
    {
        var store1 = new BackupHistoryStore(_tmpDir);
        store1.Append(MakeRecord("p1"));
        store1.Append(MakeRecord("p2"));
        store1.Append(MakeRecord("p3"));

        var store2 = new BackupHistoryStore(_tmpDir);
        var results = store2.Query();
        Assert.AreEqual(3, results.Count);
    }
}
