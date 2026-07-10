using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Resources.Models;
using ShipRight.Modules.Resources.Stores;

namespace ShipRight.Tests.Modules.Resources;

[TestClass]
public class PipelineRouterTests : IDisposable
{
    private readonly string _tmpDir;

    public PipelineRouterTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"sr_router_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_tmpDir, recursive: true);
    }

    [TestMethod]
    public async Task PipelineStore_GetAllAsync_ReturnsAllPipelines()
    {
        var store = new SqlitePipelineResourceStore(_tmpDir);
        var pipeline = new PipelineResource
        {
            Name = "test pipeline",
            Steps = [new() { Id = Guid.NewGuid(), Type = PipelineStepType.Build }],
            Scope = PipelineScope.Global,
            CreatedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow,
        };
        await store.SaveAsync(pipeline);

        var all = await store.GetAllAsync();
        Assert.AreEqual(1, all.Count);
        Assert.AreEqual("test pipeline", all[0].Name);
    }

    [TestMethod]
    public async Task PipelineStore_GetGlobalAsync_ReturnsOnlyGlobal()
    {
        var store = new SqlitePipelineResourceStore(_tmpDir);
        await store.SaveAsync(new PipelineResource
        {
            Name = "global",
            Scope = PipelineScope.Global,
            CreatedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow,
        });
        await store.SaveAsync(new PipelineResource
        {
            Name = "project",
            Scope = PipelineScope.Project,
            ProjectId = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow,
        });

        var globals = await store.GetGlobalAsync();
        Assert.AreEqual(1, globals.Count);
        Assert.AreEqual("global", globals[0].Name);
    }

    [TestMethod]
    public async Task PipelineStore_GetByProjectAsync_ReturnsOnlyProjectPipelines()
    {
        var store = new SqlitePipelineResourceStore(_tmpDir);
        var projectId = Guid.NewGuid();
        await store.SaveAsync(new PipelineResource
        {
            Name = "global",
            Scope = PipelineScope.Global,
            CreatedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow,
        });
        await store.SaveAsync(new PipelineResource
        {
            Name = "project pipeline",
            Scope = PipelineScope.Project,
            ProjectId = projectId,
            CreatedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow,
        });

        var projectPipelines = await store.GetByProjectAsync(projectId);
        Assert.AreEqual(1, projectPipelines.Count);
        Assert.AreEqual("project pipeline", projectPipelines[0].Name);
    }

    [TestMethod]
    public async Task PipelineStore_DeleteAsync_RemovesPipeline()
    {
        var store = new SqlitePipelineResourceStore(_tmpDir);
        var pipeline = new PipelineResource
        {
            Name = "to delete",
            Scope = PipelineScope.Global,
            CreatedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow,
        };
        await store.SaveAsync(pipeline);

        await store.DeleteAsync(pipeline.Id);

        var all = await store.GetAllAsync();
        Assert.AreEqual(0, all.Count);
    }

    [TestMethod]
    public async Task PipelineStore_SaveAsync_UpdatesExistingPipeline()
    {
        var store = new SqlitePipelineResourceStore(_tmpDir);
        var pipeline = new PipelineResource
        {
            Name = "original",
            Scope = PipelineScope.Global,
            CreatedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow,
        };
        await store.SaveAsync(pipeline);

        var updated = pipeline with { Name = "updated" };
        await store.SaveAsync(updated);

        var found = await store.GetByIdAsync(pipeline.Id);
        Assert.IsNotNull(found);
        Assert.AreEqual("updated", found.Name);
    }

    [TestMethod]
    public async Task ScriptStore_GetGlobalAsync_ReturnsOnlyGlobalScripts()
    {
        var store = new SqliteScriptResourceStore(_tmpDir);
        await store.SaveAsync(new ScriptResource
        {
            Name = "global script",
            Content = "echo global",
            Scope = PipelineScope.Global,
            CreatedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow,
        });
        await store.SaveAsync(new ScriptResource
        {
            Name = "project script",
            Content = "echo project",
            Scope = PipelineScope.Project,
            ProjectId = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow,
        });

        var globals = await store.GetGlobalAsync();
        Assert.AreEqual(1, globals.Count);
        Assert.AreEqual("global script", globals[0].Name);
    }

    [TestMethod]
    public async Task ScriptStore_GetByProjectAsync_ReturnsOnlyProjectScripts()
    {
        var store = new SqliteScriptResourceStore(_tmpDir);
        var projectId = Guid.NewGuid();
        await store.SaveAsync(new ScriptResource
        {
            Name = "global script",
            Content = "echo global",
            Scope = PipelineScope.Global,
            CreatedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow,
        });
        await store.SaveAsync(new ScriptResource
        {
            Name = "project script",
            Content = "echo project",
            Scope = PipelineScope.Project,
            ProjectId = projectId,
            CreatedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow,
        });

        var projectScripts = await store.GetByProjectAsync(projectId);
        Assert.AreEqual(1, projectScripts.Count);
        Assert.AreEqual("project script", projectScripts[0].Name);
    }

    [TestMethod]
    public async Task PipelineStore_Count_ReflectsStoredPipelines()
    {
        var store = new SqlitePipelineResourceStore(_tmpDir);
        Assert.AreEqual(0, store.Count);

        await store.SaveAsync(new PipelineResource
        {
            Name = "one",
            Scope = PipelineScope.Global,
            CreatedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow,
        });
        Assert.AreEqual(1, store.Count);

        await store.SaveAsync(new PipelineResource
        {
            Name = "two",
            Scope = PipelineScope.Global,
            CreatedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow,
        });
        Assert.AreEqual(2, store.Count);
    }

    [TestMethod]
    public async Task PipelineStore_GetByIdAsync_ReturnsNullForNonexistent()
    {
        var store = new SqlitePipelineResourceStore(_tmpDir);
        var found = await store.GetByIdAsync(Guid.NewGuid());
        Assert.IsNull(found);
    }

    [TestMethod]
    public async Task ScriptStore_Count_ReflectsStoredScripts()
    {
        var store = new SqliteScriptResourceStore(_tmpDir);
        Assert.AreEqual(0, store.Count);

        await store.SaveAsync(new ScriptResource
        {
            Name = "one",
            Content = "echo one",
            Scope = PipelineScope.Global,
            CreatedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow,
        });
        Assert.AreEqual(1, store.Count);

        await store.SaveAsync(new ScriptResource
        {
            Name = "two",
            Content = "echo two",
            Scope = PipelineScope.Global,
            CreatedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow,
        });
        Assert.AreEqual(2, store.Count);
    }
}
