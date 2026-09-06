using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Resources.Models;
using ShipRight.Modules.Resources.Stores;

namespace ShipRight.Tests.Modules.Resources;

[TestClass]
public class ResourceTagsTests : IDisposable
{
    private readonly string _tmpDir;

    public ResourceTagsTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"sr_tags_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_tmpDir, recursive: true);
    }

    [TestMethod]
    public async Task RegistryTags_RoundTripThroughStore()
    {
        var store = new SqliteDockerRegistryResourceStore(_tmpDir);
        var resource = new DockerRegistryResource
        {
            Name = "prod ecr",
            Registry = "123.dkr.ecr.us-east-1.amazonaws.com",
            Tags = ["ecr", "prod", "aws"],
            CreatedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow,
        };
        await store.SaveAsync(resource);

        var store2 = new SqliteDockerRegistryResourceStore(_tmpDir);
        var reloaded = await store2.GetAllAsync();

        Assert.AreEqual(1, reloaded.Count);
        CollectionAssert.AreEquivalent(
            new[] { "ecr", "prod", "aws" }, reloaded[0].Tags.ToArray());
    }

    [TestMethod]
    public async Task CredentialTags_RoundTripThroughStore()
    {
        var store = new SqliteCredentialResourceStore(_tmpDir);
        var resource = new CredentialResource
        {
            Name = "github pat",
            Value = "ghp_xxx",
            Tags = ["git", "github"],
            CreatedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow,
        };
        await store.SaveAsync(resource);

        var store2 = new SqliteCredentialResourceStore(_tmpDir);
        var reloaded = await store2.GetAllAsync();

        Assert.AreEqual(1, reloaded.Count);
        CollectionAssert.AreEquivalent(new[] { "git", "github" }, reloaded[0].Tags.ToArray());
    }

    [TestMethod]
    public async Task RegistryTags_EmptyByDefault_ForLegacyPayloads()
    {
        var store = new SqliteDockerRegistryResourceStore(_tmpDir);
        var resource = new DockerRegistryResource
        {
            Name = "ghcr",
            Registry = "ghcr.io",
            CreatedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow,
        };
        await store.SaveAsync(resource);

        var store2 = new SqliteDockerRegistryResourceStore(_tmpDir);
        var reloaded = await store2.GetAllAsync();
        Assert.IsNotNull(reloaded[0].Tags);
        Assert.AreEqual(0, reloaded[0].Tags.Count);
    }
}