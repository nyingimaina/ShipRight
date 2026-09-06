using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Resources.Models;
using ShipRight.Modules.Resources.Stores;

namespace ShipRight.Tests.Modules.Resources;

[TestClass]
public class SqliteAwsProfileResourceStoreTests : IDisposable
{
    private readonly string _tmpDir;

    public SqliteAwsProfileResourceStoreTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"sr_aws_profile_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_tmpDir, recursive: true);
    }

    private static AwsProfileResource MakeNamedProfile(string name = "prod deploy") => new()
    {
        Name = name,
        ProfileName = "shipright-prod",
        DefaultRegion = "eu-west-1",
        CreatedAt = DateTime.UtcNow,
        ModifiedAt = DateTime.UtcNow,
    };

    private static AwsProfileResource MakeExplicitProfile(string name = "prod keys") => new()
    {
        Name = name,
        AccessKeyId = "AKIATESTKEYID",
        SecretAccessKey = "super-secret-key-123",
        SessionToken = "",
        CreatedAt = DateTime.UtcNow,
        ModifiedAt = DateTime.UtcNow,
    };

    [TestMethod]
    public async Task Save_AppearsInGetAll()
    {
        var store = new SqliteAwsProfileResourceStore(_tmpDir);
        await store.SaveAsync(MakeNamedProfile());

        var all = await store.GetAllAsync();

        Assert.AreEqual(1, all.Count);
        Assert.AreEqual("prod deploy", all[0].Name);
        Assert.AreEqual("shipright-prod", all[0].ProfileName);
    }

    [TestMethod]
    public async Task Save_SurvivesRestart()
    {
        var store1 = new SqliteAwsProfileResourceStore(_tmpDir);
        var profile = MakeNamedProfile();
        await store1.SaveAsync(profile);

        var store2 = new SqliteAwsProfileResourceStore(_tmpDir);
        var all = await store2.GetAllAsync();

        Assert.AreEqual(1, all.Count);
        Assert.AreEqual(profile.Id, all[0].Id);
        Assert.AreEqual("shipright-prod", all[0].ProfileName);
    }

    [TestMethod]
    public async Task Delete_RemovesFromMemoryAndDisk()
    {
        var store1 = new SqliteAwsProfileResourceStore(_tmpDir);
        var profile = MakeNamedProfile();
        await store1.SaveAsync(profile);

        await store1.DeleteAsync(profile.Id);

        var inMemory = await store1.GetAllAsync();
        Assert.AreEqual(0, inMemory.Count);

        var store2 = new SqliteAwsProfileResourceStore(_tmpDir);
        Assert.AreEqual(0, (await store2.GetAllAsync()).Count);
    }

    [TestMethod]
    public async Task Update_ReplacesExistingAndPersists()
    {
        var store = new SqliteAwsProfileResourceStore(_tmpDir);
        var profile = MakeNamedProfile("original");
        await store.SaveAsync(profile);

        var updated = profile with { Name = "updated profile" };
        await store.SaveAsync(updated);

        var store2 = new SqliteAwsProfileResourceStore(_tmpDir);
        var reloaded = await store2.GetAllAsync();
        Assert.AreEqual(1, reloaded.Count);
        Assert.AreEqual("updated profile", reloaded[0].Name);
    }

    [TestMethod]
    public async Task GetByIdAsync_ReturnsCorrectProfile()
    {
        var store = new SqliteAwsProfileResourceStore(_tmpDir);
        var p1 = MakeNamedProfile("first");
        var p2 = MakeExplicitProfile("second");
        await store.SaveAsync(p1);
        await store.SaveAsync(p2);

        var found = await store.GetByIdAsync(p1.Id);
        Assert.IsNotNull(found);
        Assert.AreEqual("first", found.Name);
    }

    [TestMethod]
    public async Task GetByNameAsync_IsCaseInsensitive()
    {
        var store = new SqliteAwsProfileResourceStore(_tmpDir);
        await store.SaveAsync(MakeNamedProfile("My Profile"));

        var found = await store.GetByNameAsync("my profile");
        Assert.IsNotNull(found);
    }

    [TestMethod]
    public async Task SecretAccessKey_IsEncryptedOnDisk()
    {
        var store = new SqliteAwsProfileResourceStore(_tmpDir);
        await store.SaveAsync(MakeExplicitProfile());

        var dbPath = Path.Combine(_tmpDir, "resources.db");
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT data FROM aws_profile_resources LIMIT 1";
        var rawData = (string)cmd.ExecuteScalar()!;
        Assert.IsFalse(rawData.Contains("super-secret-key-123"),
            "SecretAccessKey should be encrypted at rest, not stored as plaintext");
    }

    [TestMethod]
    public async Task SecretAccessKey_IsDecryptedOnRead()
    {
        var store = new SqliteAwsProfileResourceStore(_tmpDir);
        await store.SaveAsync(MakeExplicitProfile());

        var all = await store.GetAllAsync();
        Assert.AreEqual("super-secret-key-123", all[0].SecretAccessKey);
    }

    [TestMethod]
    public async Task UsesExplicitKeys_DetectsAccessKeyAndSecret()
    {
        var profile = MakeExplicitProfile();
        Assert.IsTrue(profile.UsesExplicitKeys);

        var named = MakeNamedProfile();
        Assert.IsFalse(named.UsesExplicitKeys);
    }
}