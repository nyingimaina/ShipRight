using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Resources.Models;
using ShipRight.Modules.Resources.Stores;

namespace ShipRight.Tests.Modules.Resources;

[TestClass]
public class SqliteScriptResourceStoreTests : IDisposable
{
    private readonly string _tmpDir;

    public SqliteScriptResourceStoreTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"sr_script_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_tmpDir, recursive: true);
    }

    private static ScriptResource MakeResource(string name = "rebuild script", string content = "#!/bin/bash\necho hello") => new()
    {
        Name = name,
        Content = content,
        CreatedAt = DateTime.UtcNow,
        ModifiedAt = DateTime.UtcNow,
    };

    [TestMethod]
    public async Task Save_AppearsInGetAll()
    {
        var store = new SqliteScriptResourceStore(_tmpDir);
        var resource = MakeResource();
        await store.SaveAsync(resource);

        var all = await store.GetAllAsync();

        Assert.AreEqual(1, all.Count);
        Assert.AreEqual("rebuild script", all[0].Name);
        Assert.AreEqual("#!/bin/bash\necho hello", all[0].Content);
    }

    [TestMethod]
    public async Task Save_WritesSqliteToDisk()
    {
        var store = new SqliteScriptResourceStore(_tmpDir);
        await store.SaveAsync(MakeResource());

        var dbPath = Path.Combine(_tmpDir, "script_resources.db");
        Assert.IsTrue(File.Exists(dbPath), "script_resources.db should exist on disk");
    }

    [TestMethod]
    public async Task Save_SurvivesRestart()
    {
        var store1 = new SqliteScriptResourceStore(_tmpDir);
        var resource = MakeResource();
        await store1.SaveAsync(resource);

        var store2 = new SqliteScriptResourceStore(_tmpDir);
        var all = await store2.GetAllAsync();

        Assert.AreEqual(1, all.Count);
        Assert.AreEqual(resource.Id, all[0].Id);
        Assert.AreEqual("rebuild script", all[0].Name);
    }

    [TestMethod]
    public async Task Save_MultipleResources_AllPersistedAndReloaded()
    {
        var store1 = new SqliteScriptResourceStore(_tmpDir);
        await store1.SaveAsync(MakeResource("rebuild", "echo rebuild"));
        await store1.SaveAsync(MakeResource("deploy", "echo deploy"));

        var store2 = new SqliteScriptResourceStore(_tmpDir);
        var all = await store2.GetAllAsync();

        Assert.AreEqual(2, all.Count);
        Assert.IsTrue(all.Any(r => r.Name == "rebuild"));
        Assert.IsTrue(all.Any(r => r.Name == "deploy"));
    }

    [TestMethod]
    public async Task Delete_RemovesFromMemoryAndDisk()
    {
        var store1 = new SqliteScriptResourceStore(_tmpDir);
        var resource = MakeResource();
        await store1.SaveAsync(resource);

        await store1.DeleteAsync(resource.Id);

        var inMemory = await store1.GetAllAsync();
        Assert.AreEqual(0, inMemory.Count);

        var store2 = new SqliteScriptResourceStore(_tmpDir);
        var reloaded = await store2.GetAllAsync();
        Assert.AreEqual(0, reloaded.Count);
    }

    [TestMethod]
    public async Task Update_ReplacesExistingAndPersists()
    {
        var store = new SqliteScriptResourceStore(_tmpDir);
        var resource = MakeResource("original", "echo original");
        await store.SaveAsync(resource);

        var updated = resource with { Name = "updated", Content = "echo updated" };
        await store.SaveAsync(updated);

        var all = await store.GetAllAsync();
        Assert.AreEqual(1, all.Count);
        Assert.AreEqual("updated", all[0].Name);
        Assert.AreEqual("echo updated", all[0].Content);

        var store2 = new SqliteScriptResourceStore(_tmpDir);
        var reloaded = await store2.GetAllAsync();
        Assert.AreEqual("updated", reloaded[0].Name);
    }

    [TestMethod]
    public async Task GetByIdAsync_ReturnsCorrectResource()
    {
        var store = new SqliteScriptResourceStore(_tmpDir);
        var resource1 = MakeResource("first", "echo first");
        var resource2 = MakeResource("second", "echo second");
        await store.SaveAsync(resource1);
        await store.SaveAsync(resource2);

        var found = await store.GetByIdAsync(resource1.Id);
        Assert.IsNotNull(found);
        Assert.AreEqual("first", found.Name);
    }

    [TestMethod]
    public async Task GetByIdAsync_WhenNotFound_ReturnsNull()
    {
        var store = new SqliteScriptResourceStore(_tmpDir);
        var found = await store.GetByIdAsync(Guid.NewGuid());
        Assert.IsNull(found);
    }

    [TestMethod]
    public async Task GetByNameAsync_IsCaseInsensitive()
    {
        var store = new SqliteScriptResourceStore(_tmpDir);
        var resource = MakeResource("My Script");
        await store.SaveAsync(resource);

        var found = await store.GetByNameAsync("my script");
        Assert.IsNotNull(found);
        Assert.AreEqual(resource.Id, found.Id);
    }

    [TestMethod]
    public async Task Count_ReflectsStoredResources()
    {
        var store = new SqliteScriptResourceStore(_tmpDir);
        Assert.AreEqual(0, store.Count);

        await store.SaveAsync(MakeResource("one"));
        await store.SaveAsync(MakeResource("two"));
        Assert.AreEqual(2, store.Count);
    }

    [TestMethod]
    public async Task Content_SurvivesMultiLineScript()
    {
        var store = new SqliteScriptResourceStore(_tmpDir);
        var content = "#!/bin/bash\nset -e\necho 'step 1'\ndocker compose pull\ndocker compose up -d\necho 'done'";
        var resource = MakeResource("deploy script", content);
        await store.SaveAsync(resource);

        var all = await store.GetAllAsync();
        Assert.AreEqual(content, all[0].Content);
    }
}
