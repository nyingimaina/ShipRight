using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Projects;

namespace ShipRight.Tests.Modules.Projects;

// Contract integration tests — the same behaviours as JsonProjectStoreTests
// must hold for SqliteProjectStore.
[TestClass]
public class SqliteProjectStoreTests : IDisposable
{
    private readonly string _tmpDir;

    public SqliteProjectStoreTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"sr_sqlite_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        // Release pooled SQLite connections before deleting the temp directory.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_tmpDir, recursive: true);
    }

    private static ProjectConfig MakeProject(string id, string name) => new()
    {
        Id = id,
        Name = name,
        Services =
        [
            new ServiceConfig
            {
                Name = "api",
                VersionFilePath = "/tmp/v.txt",
                BuildContextPath = "/tmp",
                DockerImageName = "test/api",
            },
        ],
        GitRepos = [new GitConfig { RepoPath = "/tmp", DeployBranch = "main" }],
        Wsl = new WslConfig { WorkingDir = "/home/test" },
        Server = new ServerConfig
        {
            Host = "1.2.3.4",
            Username = "ubuntu",
            SshKeyPath = "/tmp/k.pem",
            RemoteWorkingDir = "/home/ubuntu",
            RebuildScript = "rebuild.sh",
        },
        CreatedAt = DateTime.UtcNow,
        ModifiedAt = DateTime.UtcNow,
    };

    // ── Contract tests (mirror JsonProjectStoreTests) ──────────────────────

    [TestMethod]
    public async Task Save_AppearsInGetAll()
    {
        var store = new SqliteProjectStore(_tmpDir);
        await store.SaveAsync(MakeProject("alpha", "Alpha"));

        var all = await store.GetAllAsync();

        Assert.AreEqual(1, all.Count);
        Assert.AreEqual("alpha", all[0].Id);
        Assert.AreEqual("Alpha", all[0].Name);
    }

    [TestMethod]
    public async Task Save_WritesSqliteToDisk()
    {
        var store = new SqliteProjectStore(_tmpDir);
        await store.SaveAsync(MakeProject("beta", "Beta"));

        var dbPath = Path.Combine(_tmpDir, "projects.db");
        Assert.IsTrue(File.Exists(dbPath), "projects.db should exist on disk");
    }

    [TestMethod]
    public async Task Save_SurvivesRestart()
    {
        var store1 = new SqliteProjectStore(_tmpDir);
        await store1.SaveAsync(MakeProject("gamma", "Gamma"));

        var store2 = new SqliteProjectStore(_tmpDir);
        var all = await store2.GetAllAsync();

        Assert.AreEqual(1, all.Count);
        Assert.AreEqual("gamma", all[0].Id);
        Assert.AreEqual("Gamma", all[0].Name);
    }

    [TestMethod]
    public async Task Save_MultipleProjects_AllPersistedAndReloaded()
    {
        var store1 = new SqliteProjectStore(_tmpDir);
        await store1.SaveAsync(MakeProject("proj-1", "Project One"));
        await store1.SaveAsync(MakeProject("proj-2", "Project Two"));

        var store2 = new SqliteProjectStore(_tmpDir);
        var all = await store2.GetAllAsync();

        Assert.AreEqual(2, all.Count);
        Assert.IsTrue(all.Any(p => p.Id == "proj-1"));
        Assert.IsTrue(all.Any(p => p.Id == "proj-2"));
    }

    [TestMethod]
    public async Task Delete_RemovesFromMemoryAndDisk()
    {
        var store1 = new SqliteProjectStore(_tmpDir);
        await store1.SaveAsync(MakeProject("to-delete", "Temp"));

        await store1.DeleteAsync("to-delete");

        var inMemory = await store1.GetAllAsync();
        Assert.AreEqual(0, inMemory.Count);

        var store2 = new SqliteProjectStore(_tmpDir);
        var reloaded = await store2.GetAllAsync();
        Assert.AreEqual(0, reloaded.Count);
    }

    [TestMethod]
    public async Task Update_ReplacesExistingAndPersists()
    {
        var store = new SqliteProjectStore(_tmpDir);
        await store.SaveAsync(MakeProject("upd", "Original"));

        await store.SaveAsync(MakeProject("upd", "Updated Name"));

        var all = await store.GetAllAsync();
        Assert.AreEqual(1, all.Count);
        Assert.AreEqual("Updated Name", all[0].Name);

        var store2 = new SqliteProjectStore(_tmpDir);
        var reloaded = await store2.GetAllAsync();
        Assert.AreEqual("Updated Name", reloaded[0].Name);
    }

    [TestMethod]
    public async Task GetByIdAsync_ReturnsCorrectProject()
    {
        var store = new SqliteProjectStore(_tmpDir);
        await store.SaveAsync(MakeProject("find-me", "FindMe"));
        await store.SaveAsync(MakeProject("not-me", "NotMe"));

        var found = await store.GetByIdAsync("find-me");
        Assert.IsNotNull(found);
        Assert.AreEqual("FindMe", found.Name);
    }

    [TestMethod]
    public async Task GetByNameAsync_IsCaseInsensitive()
    {
        var store = new SqliteProjectStore(_tmpDir);
        await store.SaveAsync(MakeProject("case-id", "MyProject"));

        var found = await store.GetByNameAsync("myproject");
        Assert.IsNotNull(found);
        Assert.AreEqual("case-id", found.Id);
    }

    [TestMethod]
    public async Task Count_ReflectsStoredProjects()
    {
        var store = new SqliteProjectStore(_tmpDir);
        Assert.AreEqual(0, store.Count);

        await store.SaveAsync(MakeProject("c1", "One"));
        await store.SaveAsync(MakeProject("c2", "Two"));
        Assert.AreEqual(2, store.Count);
    }

    // ── Migration integration test ──────────────────────────────────────────

    [TestMethod]
    public async Task MigratesFromJsonOnFirstRun_AllProjectsReadableFromSqlite()
    {
        // Arrange: write a projects.json with two projects (JSON format, no encryption)
        var projects = new[]
        {
            MakeProject("json-1", "FromJson1"),
            MakeProject("json-2", "FromJson2"),
        };
        var json = Newtonsoft.Json.JsonConvert.SerializeObject(projects, Newtonsoft.Json.Formatting.Indented);
        var jsonPath = Path.Combine(_tmpDir, "projects.json");
        await File.WriteAllTextAsync(jsonPath, json);

        // Act: open SqliteProjectStore — it should detect and migrate
        var store = new SqliteProjectStore(_tmpDir);

        // Assert: data is in SQLite
        var all = await store.GetAllAsync();
        Assert.AreEqual(2, all.Count, "Both projects must be migrated");
        Assert.IsTrue(all.Any(p => p.Id == "json-1" && p.Name == "FromJson1"));
        Assert.IsTrue(all.Any(p => p.Id == "json-2" && p.Name == "FromJson2"));

        // Assert: projects.json was archived, not just left in place
        Assert.IsFalse(File.Exists(jsonPath), "projects.json should be removed after migration");
        Assert.IsTrue(File.Exists(jsonPath + ".migrated"), "projects.json.migrated should exist");

        // Assert: data survives a fresh store instance (reads from SQLite, not JSON)
        var store2 = new SqliteProjectStore(_tmpDir);
        var reloaded = await store2.GetAllAsync();
        Assert.AreEqual(2, reloaded.Count, "Data must survive restart after migration");
    }

    [TestMethod]
    public async Task SecondOpenAfterMigration_DoesNotDuplicateProjects()
    {
        // Arrange: seed JSON and migrate
        var jsonPath = Path.Combine(_tmpDir, "projects.json");
        await File.WriteAllTextAsync(jsonPath,
            Newtonsoft.Json.JsonConvert.SerializeObject(new[] { MakeProject("dup-1", "One") }));
        _ = new SqliteProjectStore(_tmpDir); // first open triggers migration

        // Act: open again — no JSON file remains, so no migration, no duplication
        var store2 = new SqliteProjectStore(_tmpDir);
        var all = await store2.GetAllAsync();

        Assert.AreEqual(1, all.Count, "Must not duplicate on second open");
    }
}
