using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Projects;

namespace ShipRight.Tests.Modules.Projects;

[TestClass]
public class ServiceSettingsTests
{
    private sealed class MemoryStore : IProjectStore
    {
        public Dictionary<string, ProjectConfig> Items { get; } = [];
        public int Count => Items.Count;
        public Task<List<ProjectConfig>> GetAllAsync() => Task.FromResult(Items.Values.ToList());
        public Task<ProjectConfig?> GetByIdAsync(string id) => Task.FromResult(Items.GetValueOrDefault(id));
        public Task<ProjectConfig?> GetByNameAsync(string name) => Task.FromResult(Items.Values.FirstOrDefault(p => p.Name == name));
        public Task SaveAsync(ProjectConfig project) { Items[project.Id] = project; return Task.CompletedTask; }
        public Task DeleteAsync(string id) { Items.Remove(id); return Task.CompletedTask; }
    }

    private static ProjectConfig Project(params ServiceConfig[] services) => new() { Id = "p1", Name = "Shop", Services = [.. services] };

    // ── the store wrapper ─────────────────────────────────────────────

    [TestMethod]
    public async Task SavingFromAClientThatOmitsThePerServiceSettings_KeepsTheStoredOnes()
    {
        var inner = new MemoryStore();
        await inner.SaveAsync(Project(new ServiceConfig { Name = "Api", ContainerPort = 8080, SkipLocalCleanup = true }));
        var store = new ServiceSettingsPreservingProjectStore(inner);

        await store.SaveAsync(Project(new ServiceConfig { Name = "Api" }));

        var saved = inner.Items["p1"].Services.Single();
        Assert.AreEqual(8080, saved.ContainerPort);
        Assert.AreEqual(true, saved.SkipLocalCleanup);
    }

    [TestMethod]
    public async Task ValuesTheClientDoesSend_AreNotOverridden()
    {
        var inner = new MemoryStore();
        await inner.SaveAsync(Project(new ServiceConfig { Name = "Api", ContainerPort = 8080, SkipLocalCleanup = true }));
        var store = new ServiceSettingsPreservingProjectStore(inner);

        await store.SaveAsync(Project(new ServiceConfig { Name = "Api", ContainerPort = 9090, SkipLocalCleanup = false }));

        var saved = inner.Items["p1"].Services.Single();
        Assert.AreEqual(9090, saved.ContainerPort);
        Assert.AreEqual(false, saved.SkipLocalCleanup);
    }

    [TestMethod]
    public async Task ServicesAreMatchedByName_AndNewOnesAreLeftAlone()
    {
        var inner = new MemoryStore();
        await inner.SaveAsync(Project(new ServiceConfig { Name = "Api", ContainerPort = 8080 }));
        var store = new ServiceSettingsPreservingProjectStore(inner);

        await store.SaveAsync(Project(new ServiceConfig { Name = "Web" }, new ServiceConfig { Name = "api" }));

        var saved = inner.Items["p1"].Services;
        Assert.IsNull(saved.Single(s => s.Name == "Web").ContainerPort);
        Assert.AreEqual(8080, saved.Single(s => s.Name == "api").ContainerPort, "names match ignoring case");
    }

    [TestMethod]
    public async Task ABrandNewProject_IsSavedAsIs()
    {
        var inner = new MemoryStore();

        await new ServiceSettingsPreservingProjectStore(inner).SaveAsync(Project(new ServiceConfig { Name = "Api", ContainerPort = 80 }));

        Assert.AreEqual(80, inner.Items["p1"].Services.Single().ContainerPort);
    }

    // ── the endpoint ──────────────────────────────────────────────────

    [TestMethod]
    public async Task OptOut_IsSavedOnTheNamedServiceOnly()
    {
        var store = new MemoryStore();
        await store.SaveAsync(Project(new ServiceConfig { Name = "Api" }, new ServiceConfig { Name = "Web" }));

        var result = await ProjectLocalCleanupRoutes.HandleSetAsync("p1", new LocalCleanupRequest("Api", true), store);

        Assert.IsInstanceOfType<IStatusCodeHttpResult>(result);
        Assert.AreEqual(200, ((IStatusCodeHttpResult)result).StatusCode);
        var s = store.Items["p1"].Services;
        Assert.AreEqual(true, s.Single(x => x.Name == "Api").SkipLocalCleanup);
        Assert.IsNull(s.Single(x => x.Name == "Web").SkipLocalCleanup);
    }

    [TestMethod]
    public async Task OptingBackIn_StoresFalse()
    {
        var store = new MemoryStore();
        await store.SaveAsync(Project(new ServiceConfig { Name = "Api", SkipLocalCleanup = true }));

        await ProjectLocalCleanupRoutes.HandleSetAsync("p1", new LocalCleanupRequest("Api", false), store);

        Assert.AreEqual(false, store.Items["p1"].Services.Single().SkipLocalCleanup);
    }

    [TestMethod]
    public async Task UnknownProjectOrService_IsNotFound_AndMissingFieldsAreRejected()
    {
        var store = new MemoryStore();
        await store.SaveAsync(Project(new ServiceConfig { Name = "Api" }));

        Assert.AreEqual(404, ((IStatusCodeHttpResult)await ProjectLocalCleanupRoutes.HandleSetAsync("nope", new("Api", true), store)).StatusCode);
        Assert.AreEqual(404, ((IStatusCodeHttpResult)await ProjectLocalCleanupRoutes.HandleSetAsync("p1", new("Ghost", true), store)).StatusCode);
        Assert.AreEqual(400, ((IStatusCodeHttpResult)await ProjectLocalCleanupRoutes.HandleSetAsync("p1", new(null, true), store)).StatusCode);
        Assert.AreEqual(400, ((IStatusCodeHttpResult)await ProjectLocalCleanupRoutes.HandleSetAsync("p1", new("Api", null), store)).StatusCode);
    }
}
