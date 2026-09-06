using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Resources.Models;
using ShipRight.Modules.Resources.Stores;

namespace ShipRight.Tests.Modules.Resources;

[TestClass]
public class SqliteDockerRegistryResourceStoreTests : IDisposable
{
    private readonly string _tmpDir;

    public SqliteDockerRegistryResourceStoreTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"sr_registry_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_tmpDir, recursive: true);
    }

    private static DockerRegistryResource MakeResource(string name = "company ghcr", string registry = "ghcr.io") => new()
    {
        Name = name,
        Registry = registry,
        Username = "testuser",
        Password = "testpass123",
        CreatedAt = DateTime.UtcNow,
        ModifiedAt = DateTime.UtcNow,
    };

    [TestMethod]
    public async Task Save_AppearsInGetAll()
    {
        var store = new SqliteDockerRegistryResourceStore(_tmpDir);
        var resource = MakeResource();
        await store.SaveAsync(resource);

        var all = await store.GetAllAsync();

        Assert.AreEqual(1, all.Count);
        Assert.AreEqual("company ghcr", all[0].Name);
        Assert.AreEqual("ghcr.io", all[0].Registry);
    }

    [TestMethod]
    public async Task Save_WritesSqliteToDisk()
    {
        var store = new SqliteDockerRegistryResourceStore(_tmpDir);
        await store.SaveAsync(MakeResource());

        var dbPath = Path.Combine(_tmpDir, "resources.db");
        Assert.IsTrue(File.Exists(dbPath), "resources.db should exist on disk");
    }

    [TestMethod]
    public async Task Save_SurvivesRestart()
    {
        var store1 = new SqliteDockerRegistryResourceStore(_tmpDir);
        var resource = MakeResource();
        await store1.SaveAsync(resource);

        var store2 = new SqliteDockerRegistryResourceStore(_tmpDir);
        var all = await store2.GetAllAsync();

        Assert.AreEqual(1, all.Count);
        Assert.AreEqual(resource.Id, all[0].Id);
        Assert.AreEqual("company ghcr", all[0].Name);
    }

    [TestMethod]
    public async Task Save_MultipleResources_AllPersistedAndReloaded()
    {
        var store1 = new SqliteDockerRegistryResourceStore(_tmpDir);
        await store1.SaveAsync(MakeResource("ghcr", "ghcr.io"));
        await store1.SaveAsync(MakeResource("dockerhub", "docker.io"));

        var store2 = new SqliteDockerRegistryResourceStore(_tmpDir);
        var all = await store2.GetAllAsync();

        Assert.AreEqual(2, all.Count);
        Assert.IsTrue(all.Any(r => r.Name == "ghcr"));
        Assert.IsTrue(all.Any(r => r.Name == "dockerhub"));
    }

    [TestMethod]
    public async Task Delete_RemovesFromMemoryAndDisk()
    {
        var store1 = new SqliteDockerRegistryResourceStore(_tmpDir);
        var resource = MakeResource();
        await store1.SaveAsync(resource);

        await store1.DeleteAsync(resource.Id);

        var inMemory = await store1.GetAllAsync();
        Assert.AreEqual(0, inMemory.Count);

        var store2 = new SqliteDockerRegistryResourceStore(_tmpDir);
        var reloaded = await store2.GetAllAsync();
        Assert.AreEqual(0, reloaded.Count);
    }

    [TestMethod]
    public async Task Update_ReplacesExistingAndPersists()
    {
        var store = new SqliteDockerRegistryResourceStore(_tmpDir);
        var resource = MakeResource("original", "ghcr.io");
        await store.SaveAsync(resource);

        var updated = resource with { Name = "updated name" };
        await store.SaveAsync(updated);

        var all = await store.GetAllAsync();
        Assert.AreEqual(1, all.Count);
        Assert.AreEqual("updated name", all[0].Name);

        var store2 = new SqliteDockerRegistryResourceStore(_tmpDir);
        var reloaded = await store2.GetAllAsync();
        Assert.AreEqual("updated name", reloaded[0].Name);
    }

    [TestMethod]
    public async Task GetByIdAsync_ReturnsCorrectResource()
    {
        var store = new SqliteDockerRegistryResourceStore(_tmpDir);
        var resource1 = MakeResource("first");
        var resource2 = MakeResource("second");
        await store.SaveAsync(resource1);
        await store.SaveAsync(resource2);

        var found = await store.GetByIdAsync(resource1.Id);
        Assert.IsNotNull(found);
        Assert.AreEqual("first", found.Name);
    }

    [TestMethod]
    public async Task GetByIdAsync_WhenNotFound_ReturnsNull()
    {
        var store = new SqliteDockerRegistryResourceStore(_tmpDir);
        var found = await store.GetByIdAsync(Guid.NewGuid());
        Assert.IsNull(found);
    }

    [TestMethod]
    public async Task GetByNameAsync_IsCaseInsensitive()
    {
        var store = new SqliteDockerRegistryResourceStore(_tmpDir);
        var resource = MakeResource("My Registry");
        await store.SaveAsync(resource);

        var found = await store.GetByNameAsync("my registry");
        Assert.IsNotNull(found);
        Assert.AreEqual(resource.Id, found.Id);
    }

    [TestMethod]
    public async Task Count_ReflectsStoredResources()
    {
        var store = new SqliteDockerRegistryResourceStore(_tmpDir);
        Assert.AreEqual(0, store.Count);

        await store.SaveAsync(MakeResource("one"));
        await store.SaveAsync(MakeResource("two"));
        Assert.AreEqual(2, store.Count);
    }

    [TestMethod]
    public async Task Password_IsEncryptedOnDisk()
    {
        var store = new SqliteDockerRegistryResourceStore(_tmpDir);
        await store.SaveAsync(MakeResource());

        // Read raw SQLite data to verify password is encrypted
        var dbPath = Path.Combine(_tmpDir, "resources.db");
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT data FROM docker_registry_resources LIMIT 1";
        var rawData = (string)cmd.ExecuteScalar()!;
        Assert.IsFalse(rawData.Contains("testpass123"),
            "Password should be encrypted in SQLite, not stored as plaintext");
    }

    [TestMethod]
    public async Task Password_IsDecryptedOnRead()
    {
        var store = new SqliteDockerRegistryResourceStore(_tmpDir);
        await store.SaveAsync(MakeResource());

        var all = await store.GetAllAsync();
        Assert.AreEqual("testpass123", all[0].Password);
    }

    [TestMethod]
    public async Task Password_EmptyString_StaysEmpty()
    {
        var store = new SqliteDockerRegistryResourceStore(_tmpDir);
        var resource = MakeResource();
        resource = resource with { Password = "" };
        await store.SaveAsync(resource);

        var all = await store.GetAllAsync();
        Assert.AreEqual("", all[0].Password);
    }
}
