using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Projects;

namespace ShipRight.Tests.Modules.Projects;

[TestClass]
public class ProjectSpaceRoutesTests
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

    private static async Task<MemoryStore> StoreWith(params ServiceConfig[] services)
    {
        var s = new MemoryStore();
        await s.SaveAsync(new ProjectConfig { Id = "p1", Name = "Shop", Services = [.. services] });
        return s;
    }

    [TestMethod]
    public async Task DeleteAfterPush_On_SetsEveryServiceToRemoveItsImageOnceItIsPushed()
    {
        var store = await StoreWith(
            new ServiceConfig { Name = "Api", LocalImageKeepCount = 2 },
            new ServiceConfig { Name = "Web", LocalImageKeepCount = null, SkipLocalCleanup = true });

        var result = await ProjectSpaceRoutes.HandleSetPolicyAsync("p1", new PolicyRequest(true), store);

        Assert.AreEqual(200, ((IStatusCodeHttpResult)result).StatusCode);
        var services = store.Items["p1"].Services;
        Assert.IsTrue(services.All(s => s.LocalImageKeepCount == 0));
        Assert.IsTrue(services.All(s => s.SkipLocalCleanup != true), "a service that was set to never clean is switched back on");
    }

    [TestMethod]
    public async Task DeleteAfterPush_Off_GoesBackToTheDefaultOfKeepingTheNewestTwo()
    {
        var store = await StoreWith(new ServiceConfig { Name = "Api", LocalImageKeepCount = 0 });

        await ProjectSpaceRoutes.HandleSetPolicyAsync("p1", new PolicyRequest(false), store);

        Assert.AreEqual(2, store.Items["p1"].Services.Single().LocalImageKeepCount);
    }

    [TestMethod]
    public async Task Policy_ForAnUnknownProject_IsNotFound_AndAMissingChoiceIsRejected()
    {
        var store = await StoreWith(new ServiceConfig { Name = "Api" });

        Assert.AreEqual(404, ((IStatusCodeHttpResult)await ProjectSpaceRoutes.HandleSetPolicyAsync("nope", new PolicyRequest(true), store)).StatusCode);
        Assert.AreEqual(400, ((IStatusCodeHttpResult)await ProjectSpaceRoutes.HandleSetPolicyAsync("p1", new PolicyRequest(null), store)).StatusCode);
    }
}
