using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Projects;
using ShipRight.Modules.Servers;

namespace ShipRight.Tests.Modules.Servers;

// Contract integration tests — the behaviours of IServerStore (previously
// JsonServerStore over servers.json) must hold for SqliteServerStore.
[TestClass]
public class SqliteServerStoreTests : IDisposable
{
    private readonly string _tmpDir;

    public SqliteServerStoreTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"sr_sqlite_srv_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        // Release pooled SQLite connections before deleting the temp directory.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_tmpDir, recursive: true);
    }

    private static ServerConfig MakeServer(string id, string name) => new()
    {
        Id = id,
        Name = name,
        Host = "1.2.3.4",
        Username = "ubuntu",
        SshKeyPath = "/tmp/k.pem",
        RemoteWorkingDir = "/home/ubuntu",
        RebuildScript = "rebuild.sh",
    };

    [TestMethod]
    public async Task Save_AppearsInGetAll()
    {
        var store = new SqliteServerStore(_tmpDir);
        await store.SaveAsync(MakeServer("srv-1", "Prod"));

        var all = await store.GetAllAsync();

        Assert.AreEqual(1, all.Count);
        Assert.AreEqual("srv-1", all[0].Id);
        Assert.AreEqual("Prod", all[0].Name);
    }

    [TestMethod]
    public async Task Save_WritesSqliteToDisk()
    {
        var store = new SqliteServerStore(_tmpDir);
        await store.SaveAsync(MakeServer("srv-2", "Staging"));

        var dbPath = Path.Combine(_tmpDir, "servers.db");
        Assert.IsTrue(File.Exists(dbPath), "servers.db should exist on disk");
    }

    [TestMethod]
    public async Task Save_SurvivesRestart()
    {
        var store1 = new SqliteServerStore(_tmpDir);
        await store1.SaveAsync(MakeServer("srv-3", "Dev"));

        var store2 = new SqliteServerStore(_tmpDir);
        var all = await store2.GetAllAsync();

        Assert.AreEqual(1, all.Count);
        Assert.AreEqual("srv-3", all[0].Id);
        Assert.AreEqual("Dev", all[0].Name);
    }

    [TestMethod]
    public async Task Update_ReplacesExistingAndPersists()
    {
        var store = new SqliteServerStore(_tmpDir);
        await store.SaveAsync(MakeServer("srv-upd", "Original"));

        await store.SaveAsync(MakeServer("srv-upd", "Updated Name"));

        var all = await store.GetAllAsync();
        Assert.AreEqual(1, all.Count);
        Assert.AreEqual("Updated Name", all[0].Name);

        var store2 = new SqliteServerStore(_tmpDir);
        var reloaded = await store2.GetAllAsync();
        Assert.AreEqual("Updated Name", reloaded[0].Name);
    }

    [TestMethod]
    public async Task Delete_RemovesFromMemoryAndDisk()
    {
        var store1 = new SqliteServerStore(_tmpDir);
        await store1.SaveAsync(MakeServer("srv-del", "Temp"));

        await store1.DeleteAsync("srv-del");

        var inMemory = await store1.GetAllAsync();
        Assert.AreEqual(0, inMemory.Count);

        var store2 = new SqliteServerStore(_tmpDir);
        var reloaded = await store2.GetAllAsync();
        Assert.AreEqual(0, reloaded.Count);
    }

    [TestMethod]
    public async Task GetByIdAsync_ReturnsCorrectServer()
    {
        var store = new SqliteServerStore(_tmpDir);
        await store.SaveAsync(MakeServer("find-me", "FindMe"));
        await store.SaveAsync(MakeServer("not-me", "NotMe"));

        var found = await store.GetByIdAsync("find-me");
        Assert.IsNotNull(found);
        Assert.AreEqual("FindMe", found.Name);
    }

    // ── Migration integration tests ──────────────────────────────────────────

    [TestMethod]
    public async Task MigratesFromJsonOnFirstRun_AllServersReadableFromSqlite()
    {
        var servers = new[]
        {
            MakeServer("json-1", "FromJson1"),
            MakeServer("json-2", "FromJson2"),
        };
        var json = Newtonsoft.Json.JsonConvert.SerializeObject(servers, Newtonsoft.Json.Formatting.Indented);
        var jsonPath = Path.Combine(_tmpDir, "servers.json");
        await File.WriteAllTextAsync(jsonPath, json);

        var store = new SqliteServerStore(_tmpDir);

        var all = await store.GetAllAsync();
        Assert.AreEqual(2, all.Count, "Both servers must be migrated");
        Assert.IsTrue(all.Any(s => s.Id == "json-1" && s.Name == "FromJson1"));
        Assert.IsTrue(all.Any(s => s.Id == "json-2" && s.Name == "FromJson2"));

        Assert.IsFalse(File.Exists(jsonPath), "servers.json should be removed after migration");
        Assert.IsTrue(File.Exists(jsonPath + ".migrated"), "servers.json.migrated should exist");

        var store2 = new SqliteServerStore(_tmpDir);
        var reloaded = await store2.GetAllAsync();
        Assert.AreEqual(2, reloaded.Count, "Data must survive restart after migration");
    }

    [TestMethod]
    public async Task SecondOpenAfterMigration_DoesNotDuplicateServers()
    {
        var jsonPath = Path.Combine(_tmpDir, "servers.json");
        await File.WriteAllTextAsync(jsonPath,
            Newtonsoft.Json.JsonConvert.SerializeObject(new[] { MakeServer("dup-1", "One") }));
        _ = new SqliteServerStore(_tmpDir);

        var store2 = new SqliteServerStore(_tmpDir);
        var all = await store2.GetAllAsync();

        Assert.AreEqual(1, all.Count, "Must not duplicate on second open");
    }
}
