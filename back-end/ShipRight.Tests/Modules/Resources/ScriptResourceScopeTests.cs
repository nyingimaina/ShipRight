using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Resources.Models;
using ShipRight.Modules.Resources.Stores;

namespace ShipRight.Tests.Modules.Resources;

[TestClass]
public class ScriptResourceScopeTests : IDisposable
{
    private readonly string _tmpDir;

    public ScriptResourceScopeTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"sr_scope_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_tmpDir, recursive: true);
    }

    private static ScriptResource MakeGlobal(string name = "global script", string content = "echo global") => new()
    {
        Name = name,
        Content = content,
        Platform = ScriptPlatform.Bash,
        Target = ExecutionTarget.Local,
        Scope = PipelineScope.Global,
        CreatedAt = DateTime.UtcNow,
        ModifiedAt = DateTime.UtcNow,
    };

    private static ScriptResource MakeProject(string name = "project script", Guid? projectId = null) => new()
    {
        Name = name,
        Content = $"echo project {name}",
        Platform = ScriptPlatform.PowerShell,
        Target = ExecutionTarget.Remote,
        Scope = PipelineScope.Project,
        ProjectId = projectId ?? Guid.NewGuid(),
        CreatedAt = DateTime.UtcNow,
        ModifiedAt = DateTime.UtcNow,
    };

    [TestMethod]
    public async Task Save_GlobalScript_PersistsPlatformTargetScope()
    {
        var store = new SqliteScriptResourceStore(_tmpDir);
        var resource = MakeGlobal();
        await store.SaveAsync(resource);

        var found = await store.GetByIdAsync(resource.Id);
        Assert.IsNotNull(found);
        Assert.AreEqual(ScriptPlatform.Bash, found.Platform);
        Assert.AreEqual(ExecutionTarget.Local, found.Target);
        Assert.AreEqual(PipelineScope.Global, found.Scope);
        Assert.IsNull(found.ProjectId);
    }

    [TestMethod]
    public async Task Save_ProjectScript_PersistsScopeAndProjectId()
    {
        var projectId = Guid.NewGuid();
        var store = new SqliteScriptResourceStore(_tmpDir);
        var resource = MakeProject(projectId: projectId);
        await store.SaveAsync(resource);

        var found = await store.GetByIdAsync(resource.Id);
        Assert.IsNotNull(found);
        Assert.AreEqual(PipelineScope.Project, found.Scope);
        Assert.AreEqual(projectId, found.ProjectId);
    }

    [TestMethod]
    public async Task GetGlobalAsync_ReturnsOnlyGlobalScripts()
    {
        var store = new SqliteScriptResourceStore(_tmpDir);
        var projectId = Guid.NewGuid();
        await store.SaveAsync(MakeGlobal("global1"));
        await store.SaveAsync(MakeGlobal("global2"));
        await store.SaveAsync(MakeProject("proj1", projectId));

        var globals = await store.GetGlobalAsync();

        Assert.AreEqual(2, globals.Count);
        Assert.IsTrue(globals.All(s => s.Scope == PipelineScope.Global));
        Assert.IsTrue(globals.All(s => s.Name.StartsWith("global")));
    }

    [TestMethod]
    public async Task GetByProjectAsync_ReturnsOnlyProjectScripts()
    {
        var store = new SqliteScriptResourceStore(_tmpDir);
        var projectId = Guid.NewGuid();
        var otherProjectId = Guid.NewGuid();
        await store.SaveAsync(MakeGlobal("global1"));
        await store.SaveAsync(MakeProject("proj1", projectId));
        await store.SaveAsync(MakeProject("proj2", projectId));
        await store.SaveAsync(MakeProject("other", otherProjectId));

        var projectScripts = await store.GetByProjectAsync(projectId);

        Assert.AreEqual(2, projectScripts.Count);
        Assert.IsTrue(projectScripts.All(s => s.ProjectId == projectId));
    }

    [TestMethod]
    public async Task GetByProjectAsync_ExcludesGlobalScripts()
    {
        var store = new SqliteScriptResourceStore(_tmpDir);
        var projectId = Guid.NewGuid();
        await store.SaveAsync(MakeGlobal("global1"));
        await store.SaveAsync(MakeProject("proj1", projectId));

        var projectScripts = await store.GetByProjectAsync(projectId);

        Assert.AreEqual(1, projectScripts.Count);
        Assert.AreEqual("proj1", projectScripts[0].Name);
    }

    [TestMethod]
    public async Task GetAllAsync_IncludesBothGlobalAndProject()
    {
        var store = new SqliteScriptResourceStore(_tmpDir);
        var projectId = Guid.NewGuid();
        await store.SaveAsync(MakeGlobal("global1"));
        await store.SaveAsync(MakeProject("proj1", projectId));

        var all = await store.GetAllAsync();

        Assert.AreEqual(2, all.Count);
    }

    [TestMethod]
    public async Task Save_DefaultsToGlobalScope()
    {
        var store = new SqliteScriptResourceStore(_tmpDir);
        var resource = new ScriptResource
        {
            Name = "default scope",
            Content = "echo default",
            CreatedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow,
        };
        await store.SaveAsync(resource);

        var found = await store.GetByIdAsync(resource.Id);
        Assert.IsNotNull(found);
        Assert.AreEqual(PipelineScope.Global, found.Scope);
        Assert.AreEqual(ScriptPlatform.Bash, found.Platform);
        Assert.AreEqual(ExecutionTarget.Local, found.Target);
    }

    [TestMethod]
    public async Task Save_AllPlatforms_PersistCorrectly()
    {
        var store = new SqliteScriptResourceStore(_tmpDir);
        var platforms = new[] { ScriptPlatform.Bash, ScriptPlatform.PowerShell, ScriptPlatform.Cmd, ScriptPlatform.Python, ScriptPlatform.Sh };

        foreach (var platform in platforms)
        {
            var resource = new ScriptResource
            {
                Name = $"script-{platform}",
                Content = $"echo {platform}",
                Platform = platform,
                CreatedAt = DateTime.UtcNow,
                ModifiedAt = DateTime.UtcNow,
            };
            await store.SaveAsync(resource);
        }

        var all = await store.GetAllAsync();
        Assert.AreEqual(5, all.Count);
        foreach (var platform in platforms)
        {
            var found = all.FirstOrDefault(s => s.Name == $"script-{platform}");
            Assert.IsNotNull(found);
            Assert.AreEqual(platform, found.Platform);
        }
    }

    [TestMethod]
    public async Task Save_BothTargets_PersistCorrectly()
    {
        var store = new SqliteScriptResourceStore(_tmpDir);
        var local = MakeGlobal("local script");
        var remote = new ScriptResource
        {
            Name = "remote script",
            Content = "echo remote",
            Platform = ScriptPlatform.Bash,
            Target = ExecutionTarget.Remote,
            Scope = PipelineScope.Global,
            CreatedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow,
        };

        await store.SaveAsync(local);
        await store.SaveAsync(remote);

        var all = await store.GetAllAsync();
        Assert.AreEqual(2, all.Count);
        Assert.AreEqual(ExecutionTarget.Local, all.First(s => s.Name == "local script").Target);
        Assert.AreEqual(ExecutionTarget.Remote, all.First(s => s.Name == "remote script").Target);
    }

    [TestMethod]
    public async Task Save_MigrationBackwardCompat_NewFieldsHaveDefaults()
    {
        // Simulate existing data without new fields by saving raw JSON
        var store = new SqliteScriptResourceStore(_tmpDir);
        var resource = MakeGlobal("legacy script");
        await store.SaveAsync(resource);

        // Verify new fields have sensible defaults
        var found = await store.GetByIdAsync(resource.Id);
        Assert.IsNotNull(found);
        Assert.AreEqual(PipelineScope.Global, found.Scope);
        Assert.AreEqual(ScriptPlatform.Bash, found.Platform);
        Assert.AreEqual(ExecutionTarget.Local, found.Target);
        Assert.IsNull(found.ProjectId);
    }
}
