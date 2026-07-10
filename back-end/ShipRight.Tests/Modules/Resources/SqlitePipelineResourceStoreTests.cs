using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Projects;
using ShipRight.Modules.Resources.Models;
using ShipRight.Modules.Resources.Stores;

namespace ShipRight.Tests.Modules.Resources;

[TestClass]
public class SqlitePipelineResourceStoreTests : IDisposable
{
    private readonly string _tmpDir;

    public SqlitePipelineResourceStoreTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"sr_pipeline_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_tmpDir, recursive: true);
    }

    private static PipelineResource MakePipeline(string name = "deploy pipeline", PipelineScope scope = PipelineScope.Global) => new()
    {
        Name = name,
        Steps =
        [
            new() { Id = Guid.NewGuid(), Type = PipelineStepType.Script, Label = "pre-build", ScriptResourceId = Guid.NewGuid() },
            new() { Id = Guid.NewGuid(), Type = PipelineStepType.Build },
            new() { Id = Guid.NewGuid(), Type = PipelineStepType.Push },
            new() { Id = Guid.NewGuid(), Type = PipelineStepType.Deploy },
        ],
        Scope = scope,
        CreatedAt = DateTime.UtcNow,
        ModifiedAt = DateTime.UtcNow,
    };

    [TestMethod]
    public async Task Save_AppearsInGetAll()
    {
        var store = new SqlitePipelineResourceStore(_tmpDir);
        var pipeline = MakePipeline();
        await store.SaveAsync(pipeline);

        var all = await store.GetAllAsync();

        Assert.AreEqual(1, all.Count);
        Assert.AreEqual("deploy pipeline", all[0].Name);
        Assert.AreEqual(4, all[0].Steps.Count);
    }

    [TestMethod]
    public async Task Save_WritesSqliteToDisk()
    {
        var store = new SqlitePipelineResourceStore(_tmpDir);
        await store.SaveAsync(MakePipeline());

        var dbPath = Path.Combine(_tmpDir, "pipeline_resources.db");
        Assert.IsTrue(File.Exists(dbPath), "pipeline_resources.db should exist on disk");
    }

    [TestMethod]
    public async Task Save_SurvivesRestart()
    {
        var store1 = new SqlitePipelineResourceStore(_tmpDir);
        var pipeline = MakePipeline();
        await store1.SaveAsync(pipeline);

        var store2 = new SqlitePipelineResourceStore(_tmpDir);
        var all = await store2.GetAllAsync();

        Assert.AreEqual(1, all.Count);
        Assert.AreEqual(pipeline.Id, all[0].Id);
        Assert.AreEqual("deploy pipeline", all[0].Name);
    }

    [TestMethod]
    public async Task Save_MultiplePipelines_AllPersistedAndReloaded()
    {
        var store1 = new SqlitePipelineResourceStore(_tmpDir);
        await store1.SaveAsync(MakePipeline("pipeline 1"));
        await store1.SaveAsync(MakePipeline("pipeline 2"));

        var store2 = new SqlitePipelineResourceStore(_tmpDir);
        var all = await store2.GetAllAsync();

        Assert.AreEqual(2, all.Count);
        Assert.IsTrue(all.Any(p => p.Name == "pipeline 1"));
        Assert.IsTrue(all.Any(p => p.Name == "pipeline 2"));
    }

    [TestMethod]
    public async Task Delete_RemovesFromMemoryAndDisk()
    {
        var store1 = new SqlitePipelineResourceStore(_tmpDir);
        var pipeline = MakePipeline();
        await store1.SaveAsync(pipeline);

        await store1.DeleteAsync(pipeline.Id);

        var inMemory = await store1.GetAllAsync();
        Assert.AreEqual(0, inMemory.Count);

        var store2 = new SqlitePipelineResourceStore(_tmpDir);
        var reloaded = await store2.GetAllAsync();
        Assert.AreEqual(0, reloaded.Count);
    }

    [TestMethod]
    public async Task Update_ReplacesExistingAndPersists()
    {
        var store = new SqlitePipelineResourceStore(_tmpDir);
        var pipeline = MakePipeline("original");
        await store.SaveAsync(pipeline);

        var updated = pipeline with { Name = "updated" };
        await store.SaveAsync(updated);

        var all = await store.GetAllAsync();
        Assert.AreEqual(1, all.Count);
        Assert.AreEqual("updated", all[0].Name);

        var store2 = new SqlitePipelineResourceStore(_tmpDir);
        var reloaded = await store2.GetAllAsync();
        Assert.AreEqual("updated", reloaded[0].Name);
    }

    [TestMethod]
    public async Task GetByIdAsync_ReturnsCorrectPipeline()
    {
        var store = new SqlitePipelineResourceStore(_tmpDir);
        var p1 = MakePipeline("first");
        var p2 = MakePipeline("second");
        await store.SaveAsync(p1);
        await store.SaveAsync(p2);

        var found = await store.GetByIdAsync(p1.Id);
        Assert.IsNotNull(found);
        Assert.AreEqual("first", found.Name);
    }

    [TestMethod]
    public async Task GetByIdAsync_WhenNotFound_ReturnsNull()
    {
        var store = new SqlitePipelineResourceStore(_tmpDir);
        var found = await store.GetByIdAsync(Guid.NewGuid());
        Assert.IsNull(found);
    }

    [TestMethod]
    public async Task GetGlobalAsync_ReturnsOnlyGlobalPipelines()
    {
        var store = new SqlitePipelineResourceStore(_tmpDir);
        var projectId = Guid.NewGuid();
        await store.SaveAsync(MakePipeline("global1", PipelineScope.Global));
        await store.SaveAsync(MakePipeline("global2", PipelineScope.Global));
        await store.SaveAsync(MakePipeline("project1", PipelineScope.Project) with { ProjectId = projectId });

        var globals = await store.GetGlobalAsync();

        Assert.AreEqual(2, globals.Count);
        Assert.IsTrue(globals.All(p => p.Scope == PipelineScope.Global));
    }

    [TestMethod]
    public async Task GetByProjectAsync_ReturnsOnlyProjectPipelines()
    {
        var store = new SqlitePipelineResourceStore(_tmpDir);
        var projectId = Guid.NewGuid();
        var otherProjectId = Guid.NewGuid();
        await store.SaveAsync(MakePipeline("global", PipelineScope.Global));
        await store.SaveAsync(MakePipeline("proj1", PipelineScope.Project) with { ProjectId = projectId });
        await store.SaveAsync(MakePipeline("proj2", PipelineScope.Project) with { ProjectId = projectId });
        await store.SaveAsync(MakePipeline("other", PipelineScope.Project) with { ProjectId = otherProjectId });

        var projectPipelines = await store.GetByProjectAsync(projectId);

        Assert.AreEqual(2, projectPipelines.Count);
        Assert.IsTrue(projectPipelines.All(p => p.ProjectId == projectId));
    }

    [TestMethod]
    public async Task Steps_AreSerializedAsJson()
    {
        var store = new SqlitePipelineResourceStore(_tmpDir);
        var pipeline = MakePipeline("json steps");
        await store.SaveAsync(pipeline);

        var found = await store.GetByIdAsync(pipeline.Id);
        Assert.IsNotNull(found);
        Assert.AreEqual(4, found.Steps.Count);
        Assert.AreEqual(PipelineStepType.Script, found.Steps[0].Type);
        Assert.AreEqual(PipelineStepType.Build, found.Steps[1].Type);
        Assert.AreEqual(PipelineStepType.Push, found.Steps[2].Type);
        Assert.AreEqual(PipelineStepType.Deploy, found.Steps[3].Type);
    }

    [TestMethod]
    public async Task Steps_PreserveScriptResourceId()
    {
        var scriptId = Guid.NewGuid();
        var store = new SqlitePipelineResourceStore(_tmpDir);
        var pipeline = new PipelineResource
        {
            Name = "with script",
            Steps = [new() { Id = Guid.NewGuid(), Type = PipelineStepType.Script, ScriptResourceId = scriptId, Label = "test" }],
            CreatedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow,
        };
        await store.SaveAsync(pipeline);

        var found = await store.GetByIdAsync(pipeline.Id);
        Assert.IsNotNull(found);
        Assert.AreEqual(scriptId, found.Steps[0].ScriptResourceId);
        Assert.AreEqual("test", found.Steps[0].Label);
    }

    [TestMethod]
    public async Task Steps_PreserveDeployMode()
    {
        var store = new SqlitePipelineResourceStore(_tmpDir);
        var pipeline = new PipelineResource
        {
            Name = "deploy with mode",
            Steps = [new() { Id = Guid.NewGuid(), Type = PipelineStepType.Deploy, DeployMode = DeployMode.EnvCompose }],
            CreatedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow,
        };
        await store.SaveAsync(pipeline);

        var found = await store.GetByIdAsync(pipeline.Id);
        Assert.IsNotNull(found);
        Assert.AreEqual(DeployMode.EnvCompose, found.Steps[0].DeployMode);
    }

    [TestMethod]
    public async Task Steps_PreserveContinueOnError()
    {
        var store = new SqlitePipelineResourceStore(_tmpDir);
        var pipeline = new PipelineResource
        {
            Name = "continue on error",
            Steps = [new() { Id = Guid.NewGuid(), Type = PipelineStepType.Script, ContinueOnError = true }],
            CreatedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow,
        };
        await store.SaveAsync(pipeline);

        var found = await store.GetByIdAsync(pipeline.Id);
        Assert.IsNotNull(found);
        Assert.IsTrue(found.Steps[0].ContinueOnError);
    }

    [TestMethod]
    public async Task Count_ReflectsStoredPipelines()
    {
        var store = new SqlitePipelineResourceStore(_tmpDir);
        Assert.AreEqual(0, store.Count);

        await store.SaveAsync(MakePipeline("one"));
        await store.SaveAsync(MakePipeline("two"));
        Assert.AreEqual(2, store.Count);
    }

    [TestMethod]
    public async Task DefaultScope_IsGlobal()
    {
        var store = new SqlitePipelineResourceStore(_tmpDir);
        var pipeline = new PipelineResource
        {
            Name = "default scope",
            CreatedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow,
        };
        await store.SaveAsync(pipeline);

        var found = await store.GetByIdAsync(pipeline.Id);
        Assert.IsNotNull(found);
        Assert.AreEqual(PipelineScope.Global, found.Scope);
        Assert.AreEqual(0, found.Steps.Count);
    }
}
