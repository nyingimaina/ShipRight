using ShipRight.Modules.Projects;
using Xunit;

namespace ShipRight.Tests.Modules.Projects;

public class JsonProjectStoreTests : IDisposable
{
    private readonly string _tmpDir;

    public JsonProjectStoreTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"sr_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose() => Directory.Delete(_tmpDir, recursive: true);

    private static ProjectConfig MakeProject(string id, string name) => new()
    {
        Id = id,
        Name = name,
        Services = [new ServiceConfig { Name = "api", VersionFilePath = "/tmp/v.txt", BuildContextPath = "/tmp", DockerImageName = "test/api" }],
        GitRepos = [new GitConfig { RepoPath = "/tmp", DeployBranch = "main" }],
        Wsl = new WslConfig { WorkingDir = "/home/test" },
        Server = new ServerConfig { Host = "1.2.3.4", Username = "ubuntu", SshKeyPath = "/tmp/k.pem", RemoteWorkingDir = "/home/ubuntu", RebuildScript = "rebuild.sh" },
        CreatedAt = DateTime.UtcNow,
        ModifiedAt = DateTime.UtcNow,
    };

    [Fact]
    public async Task Save_AppearsInGetAll()
    {
        var store = new JsonProjectStore(_tmpDir);
        await store.SaveAsync(MakeProject("alpha", "Alpha"));

        var all = await store.GetAllAsync();

        Assert.Single(all);
        Assert.Equal("alpha", all[0].Id);
        Assert.Equal("Alpha", all[0].Name);
    }

    [Fact]
    public async Task Save_WritesJsonToDisk()
    {
        var store = new JsonProjectStore(_tmpDir);
        await store.SaveAsync(MakeProject("beta", "Beta"));

        var jsonPath = Path.Combine(_tmpDir, "projects.json");
        Assert.True(File.Exists(jsonPath));
        var json = await File.ReadAllTextAsync(jsonPath);
        Assert.Contains("\"beta\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Save_SurvivesRestart()
    {
        var store1 = new JsonProjectStore(_tmpDir);
        await store1.SaveAsync(MakeProject("gamma", "Gamma"));

        // Simulate backend restart by creating a fresh store instance reading the same file.
        var store2 = new JsonProjectStore(_tmpDir);
        var all = await store2.GetAllAsync();

        Assert.Single(all);
        Assert.Equal("gamma", all[0].Id);
        Assert.Equal("Gamma", all[0].Name);
    }

    [Fact]
    public async Task Save_MultipleProjects_AllPersistedAndReloaded()
    {
        var store1 = new JsonProjectStore(_tmpDir);
        await store1.SaveAsync(MakeProject("proj-1", "Project One"));
        await store1.SaveAsync(MakeProject("proj-2", "Project Two"));

        var store2 = new JsonProjectStore(_tmpDir);
        var all = await store2.GetAllAsync();

        Assert.Equal(2, all.Count);
        Assert.Contains(all, p => p.Id == "proj-1");
        Assert.Contains(all, p => p.Id == "proj-2");
    }

    [Fact]
    public async Task Delete_RemovesFromMemoryAndDisk()
    {
        var store1 = new JsonProjectStore(_tmpDir);
        await store1.SaveAsync(MakeProject("to-delete", "Temp"));

        await store1.DeleteAsync("to-delete");

        var inMemory = await store1.GetAllAsync();
        Assert.Empty(inMemory);

        var store2 = new JsonProjectStore(_tmpDir);
        var reloaded = await store2.GetAllAsync();
        Assert.Empty(reloaded);
    }

    [Fact]
    public async Task Update_ReplacesExistingAndPersists()
    {
        var store = new JsonProjectStore(_tmpDir);
        await store.SaveAsync(MakeProject("upd", "Original"));

        var updated = MakeProject("upd", "Updated Name");
        await store.SaveAsync(updated);

        var all = await store.GetAllAsync();
        Assert.Single(all);
        Assert.Equal("Updated Name", all[0].Name);

        var store2 = new JsonProjectStore(_tmpDir);
        var reloaded = await store2.GetAllAsync();
        Assert.Equal("Updated Name", reloaded[0].Name);
    }
}
