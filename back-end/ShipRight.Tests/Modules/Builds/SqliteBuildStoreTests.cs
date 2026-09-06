using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using ShipRight.Modules.Builds;

namespace ShipRight.Tests.Modules.Builds;

// Contract integration tests — the behaviours of JsonBuildStore (builds/*.json)
// must hold for SqliteBuildStore.
[TestClass]
public class SqliteBuildStoreTests : IDisposable
{
    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        Formatting = Formatting.Indented,
        Converters = { new StringEnumConverter() },
    };

    private readonly string _tmpDir;

    public SqliteBuildStoreTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"sr_sqlite_build_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        // Release pooled SQLite connections before deleting the temp directory.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_tmpDir, recursive: true);
    }

    private static BuildRecord MakeBuild(string id, string projectId, BuildStatus status = BuildStatus.Running,
        DateTime? startedAt = null, string? gitTag = null)
    {
        var record = new BuildRecord
        {
            Id = id,
            ProjectId = projectId,
            ProjectName = $"Project-{projectId}",
            Status = status,
            GitTag = gitTag ?? "v1.0",
            StartedAt = startedAt ?? DateTime.UtcNow,
        };
        record.AppendLogLine("line one");
        record.AppendLogLine("line two");
        return record;
    }

    [TestMethod]
    public async Task Save_AppearsInGetById()
    {
        var store = new SqliteBuildStore(_tmpDir);
        await store.SaveAsync(MakeBuild("b1", "p1"));

        var found = await store.GetByIdAsync("b1");
        Assert.IsNotNull(found);
        Assert.AreEqual("b1", found!.Id);
        Assert.AreEqual("p1", found.ProjectId);
    }

    [TestMethod]
    public async Task Save_WritesSqliteToDisk()
    {
        var store = new SqliteBuildStore(_tmpDir);
        await store.SaveAsync(MakeBuild("b2", "p1"));

        var dbPath = Path.Combine(_tmpDir, "builds.db");
        Assert.IsTrue(File.Exists(dbPath), "builds.db should exist on disk");
    }

    [TestMethod]
    public async Task Count_ReflectsStoredBuilds()
    {
        var store = new SqliteBuildStore(_tmpDir);
        Assert.AreEqual(0, store.Count);

        await store.SaveAsync(MakeBuild("c1", "p1"));
        await store.SaveAsync(MakeBuild("c2", "p1"));
        Assert.AreEqual(2, store.Count);
    }

    [TestMethod]
    public async Task Save_SurvivesRestart()
    {
        var store1 = new SqliteBuildStore(_tmpDir);
        await store1.SaveAsync(MakeBuild("b3", "p1"));

        var store2 = new SqliteBuildStore(_tmpDir);
        var found = await store2.GetByIdAsync("b3");

        Assert.IsNotNull(found);
        Assert.AreEqual("p1", found!.ProjectId);
    }

    [TestMethod]
    public async Task Update_ReplacesExistingAndPersists()
    {
        var store = new SqliteBuildStore(_tmpDir);
        await store.SaveAsync(MakeBuild("b4", "p1", BuildStatus.Running));

        var updated = await store.GetByIdAsync("b4");
        Assert.IsNotNull(updated);
        updated!.Status = BuildStatus.Deployed;
        updated.CompletedAt = DateTime.UtcNow;
        await store.SaveAsync(updated);

        var store2 = new SqliteBuildStore(_tmpDir);
        var reloaded = await store2.GetByIdAsync("b4");
        Assert.IsNotNull(reloaded);
        Assert.AreEqual(BuildStatus.Deployed, reloaded!.Status);
    }

    [TestMethod]
    public async Task GetByIdAsync_ReturnsNullForMissingBuild()
    {
        var store = new SqliteBuildStore(_tmpDir);
        var found = await store.GetByIdAsync("missing");
        Assert.IsNull(found);
    }

    [TestMethod]
    public async Task Logs_RoundTripThroughGetByIdAsync()
    {
        var store = new SqliteBuildStore(_tmpDir);
        await store.SaveAsync(MakeBuild("b5", "p1"));

        var reloaded = await store.GetByIdAsync("b5");
        Assert.IsNotNull(reloaded);
        Assert.AreEqual("line one\nline two", reloaded!.GetLogOutput());
    }

    [TestMethod]
    public async Task QueryAsync_FiltersByProjectId()
    {
        var store = new SqliteBuildStore(_tmpDir);
        await store.SaveAsync(MakeBuild("q1", "proj-a", startedAt: DateTime.UtcNow));
        await store.SaveAsync(MakeBuild("q2", "proj-b", startedAt: DateTime.UtcNow.AddMinutes(1)));

        var results = await store.QueryAsync("proj-a", null, null, null, null, 1, 50);
        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("q1", results[0].Id);
    }

    [TestMethod]
    public async Task QueryAsync_FiltersByStatus_SupportsCommaList()
    {
        var store = new SqliteBuildStore(_tmpDir);
        await store.SaveAsync(MakeBuild("s1", "p1", BuildStatus.Running));
        await store.SaveAsync(MakeBuild("s2", "p1", BuildStatus.Deployed));
        await store.SaveAsync(MakeBuild("s3", "p1", BuildStatus.BuildFailed));

        var results = await store.QueryAsync(null, "Deployed", null, null, null, 1, 50);
        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("s2", results[0].Id);

        var multi = await store.QueryAsync(null, "Running,BuildFailed", null, null, null, 1, 50);
        Assert.AreEqual(2, multi.Count);
    }

    [TestMethod]
    public async Task QueryAsync_FiltersByGitTag()
    {
        var store = new SqliteBuildStore(_tmpDir);
        await store.SaveAsync(MakeBuild("g1", "p1", gitTag: "v2.0"));
        await store.SaveAsync(MakeBuild("g2", "p1", gitTag: "v1.0", startedAt: DateTime.UtcNow.AddMinutes(1)));

        var results = await store.QueryAsync(null, null, null, null, "v2.0", 1, 50);
        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("g1", results[0].Id);
    }

    [TestMethod]
    public async Task QueryAsync_FiltersByDateRange()
    {
        var store = new SqliteBuildStore(_tmpDir);
        var old = DateTime.UtcNow.AddDays(-5);
        var recent = DateTime.UtcNow;
        await store.SaveAsync(MakeBuild("d1", "p1", startedAt: old));
        await store.SaveAsync(MakeBuild("d2", "p1", startedAt: recent));

        var recentOnly = await store.QueryAsync(null, null, DateTime.UtcNow.AddDays(-1), null, null, 1, 50);
        Assert.AreEqual(1, recentOnly.Count);
        Assert.AreEqual("d2", recentOnly[0].Id);
    }

    [TestMethod]
    public async Task QueryAsync_PaginatesAndOrdersNewestFirst()
    {
        var store = new SqliteBuildStore(_tmpDir);
        for (var i = 0; i < 5; i++)
            await store.SaveAsync(MakeBuild($"page-{i}", "p1", startedAt: DateTime.UtcNow.AddMinutes(i)));

        var page1 = await store.QueryAsync(null, null, null, null, null, 1, 2);
        var page2 = await store.QueryAsync(null, null, null, null, null, 2, 2);

        Assert.AreEqual(2, page1.Count);
        Assert.AreEqual(2, page2.Count);
        Assert.AreEqual("page-4", page1[0].Id, "Newest build must come first");
        Assert.AreNotEqual(page1[0].Id, page2[0].Id);
    }

    [TestMethod]
    public async Task CountQueryAsync_FiltersByStatus()
    {
        var store = new SqliteBuildStore(_tmpDir);
        await store.SaveAsync(MakeBuild("cq1", "p1", BuildStatus.Running));
        await store.SaveAsync(MakeBuild("cq2", "p1", BuildStatus.Deployed));
        await store.SaveAsync(MakeBuild("cq3", "p1", BuildStatus.BuildFailed));

        var count = await store.CountQueryAsync(null, "Running,BuildFailed", null, null, null);
        Assert.AreEqual(2, count);
    }

    [TestMethod]
    public async Task MarkInterruptedAsync_MarksOldRunningBuildsAsInterrupted()
    {
        var store = new SqliteBuildStore(_tmpDir);
        var stale = DateTime.UtcNow.AddMinutes(-10);
        await store.SaveAsync(MakeBuild("stale-1", "p1", BuildStatus.Running, startedAt: stale));
        await store.SaveAsync(MakeBuild("stale-2", "p1", BuildStatus.Deploying, startedAt: stale));

        await store.MarkInterruptedAsync();

        var b1 = await store.GetByIdAsync("stale-1");
        var b2 = await store.GetByIdAsync("stale-2");
        Assert.AreEqual(BuildStatus.Interrupted, b1!.Status);
        Assert.AreEqual(BuildStatus.Interrupted, b2!.Status);
        Assert.IsTrue(b1.GetLogOutput().Contains("marked as interrupted"));
    }

    [TestMethod]
    public async Task MarkInterruptedAsync_LeavesRecentRunningBuildsUntouched()
    {
        var store = new SqliteBuildStore(_tmpDir);
        await store.SaveAsync(MakeBuild("recent", "p1", BuildStatus.Running, startedAt: DateTime.UtcNow));
        await store.SaveAsync(MakeBuild("paused", "p1", BuildStatus.Paused, startedAt: DateTime.UtcNow.AddMinutes(-10)));

        await store.MarkInterruptedAsync();

        Assert.AreEqual(BuildStatus.Running, (await store.GetByIdAsync("recent"))!.Status);
        Assert.AreEqual(BuildStatus.Paused, (await store.GetByIdAsync("paused"))!.Status);
    }

    // ── Migration integration tests ──────────────────────────────────────────

    [TestMethod]
    public async Task MigratesFromJsonOnFirstRun_BuildsReadableFromSqlite()
    {
        var buildsDir = Path.Combine(_tmpDir, "builds");
        Directory.CreateDirectory(buildsDir);
        await File.WriteAllTextAsync(Path.Combine(buildsDir, "json-1.json"),
            JsonConvert.SerializeObject(MakeBuild("json-1", "proj-a", BuildStatus.Deployed), JsonSettings));
        await File.WriteAllTextAsync(Path.Combine(buildsDir, "json-2.json"),
            JsonConvert.SerializeObject(MakeBuild("json-2", "proj-b", BuildStatus.BuildFailed), JsonSettings));

        var store = new SqliteBuildStore(_tmpDir);

        Assert.AreEqual(2, store.Count, "Both builds must be migrated");
        Assert.AreEqual(BuildStatus.Deployed, (await store.GetByIdAsync("json-1"))!.Status);
        Assert.AreEqual(BuildStatus.BuildFailed, (await store.GetByIdAsync("json-2"))!.Status);
        Assert.AreEqual("line one\nline two", (await store.GetByIdAsync("json-1"))!.GetLogOutput());

        Assert.IsFalse(Directory.Exists(buildsDir), "builds dir should be removed after migration");
        Assert.IsTrue(Directory.Exists(buildsDir + ".migrated"), "builds.migrated should exist");

        var store2 = new SqliteBuildStore(_tmpDir);
        Assert.AreEqual(2, store2.Count, "Data must survive restart after migration");
    }

    [TestMethod]
    public async Task SecondOpenAfterMigration_DoesNotDuplicateBuilds()
    {
        var buildsDir = Path.Combine(_tmpDir, "builds");
        Directory.CreateDirectory(buildsDir);
        await File.WriteAllTextAsync(Path.Combine(buildsDir, "dup-1.json"),
            JsonConvert.SerializeObject(MakeBuild("dup-1", "p1"), JsonSettings));
        _ = new SqliteBuildStore(_tmpDir);

        var store2 = new SqliteBuildStore(_tmpDir);
        Assert.AreEqual(1, store2.Count, "Must not duplicate on second open");
    }
}
