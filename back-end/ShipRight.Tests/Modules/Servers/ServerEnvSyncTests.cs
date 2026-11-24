using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Projects;
using ShipRight.Modules.Servers;

namespace ShipRight.Tests.Modules.Servers;

/// <summary>
/// Server-level variables are saved on the server record. Every project pointing at that server must see the new values
/// in its embedded copy too, otherwise the settings screen and a deploy disagree.
/// </summary>
[TestClass]
public class ServerEnvSyncTests
{
    private sealed class Projects : IProjectStore
    {
        public Dictionary<string, ProjectConfig> Items { get; } = [];
        public int Count => Items.Count;
        public Task<List<ProjectConfig>> GetAllAsync() => Task.FromResult(Items.Values.ToList());
        public Task<ProjectConfig?> GetByIdAsync(string id) => Task.FromResult(Items.GetValueOrDefault(id));
        public Task<ProjectConfig?> GetByNameAsync(string name) => Task.FromResult(Items.Values.FirstOrDefault(p => p.Name == name));
        public Task SaveAsync(ProjectConfig p) { Items[p.Id] = p; return Task.CompletedTask; }
        public Task DeleteAsync(string id) { Items.Remove(id); return Task.CompletedTask; }
    }

    private sealed class Servers : IServerStore
    {
        public Dictionary<string, ServerConfig> Items { get; } = [];
        public Task<List<ServerConfig>> GetAllAsync() => Task.FromResult(Items.Values.ToList());
        public Task<ServerConfig?> GetByIdAsync(string id) => Task.FromResult(Items.GetValueOrDefault(id));
        public Task SaveAsync(ServerConfig s) { Items[s.Id] = s; return Task.CompletedTask; }
        public Task DeleteAsync(string id) { Items.Remove(id); return Task.CompletedTask; }
    }

    private sealed class Rig
    {
        public Projects ProjectStore { get; } = new();
        public Servers ServerStore { get; } = new();

        public Rig()
        {
            ServerStore.Items["s1"] = new ServerConfig { Id = "s1", Name = "Box", Host = "1.2.3.4", EnvFile = "OLD=1\n" };
            ServerStore.Items["s2"] = new ServerConfig { Id = "s2", Name = "Other", Host = "5.6.7.8", EnvFile = "OTHER=1\n" };

            ProjectStore.Items["p1"] = Project("p1", "s1");
            ProjectStore.Items["p2"] = Project("p2", "s1");
            ProjectStore.Items["p3"] = Project("p3", "s2");
        }

        private static ProjectConfig Project(string id, string serverId) => new()
        {
            Id = id, Name = id, ServerId = serverId,
            Server = new ServerConfig { Id = serverId, Host = "original-host", Username = "deploy", RemoteWorkingDir = "/deploy/" + id, EnvFile = "OLD=1\n" },
        };
    }

    private static int Status(IResult r) => ((IStatusCodeHttpResult)r).StatusCode ?? 0;

    [TestMethod]
    public async Task UpdatingServerVariables_RefreshesEveryReferencingProject()
    {
        var rig = new Rig();
        var updated = rig.ServerStore.Items["s1"] with { EnvFile = "NEW=2\nKEEP=yes\n" };

        var r = await ServerRouter.HandleUpdateAsync("s1", updated, rig.ServerStore, rig.ProjectStore);

        Assert.AreEqual(200, Status(r));
        Assert.AreEqual("NEW=2\nKEEP=yes\n", rig.ServerStore.Items["s1"].EnvFile);
        Assert.AreEqual("NEW=2\nKEEP=yes\n", rig.ProjectStore.Items["p1"].Server.EnvFile);
        Assert.AreEqual("NEW=2\nKEEP=yes\n", rig.ProjectStore.Items["p2"].Server.EnvFile);
        Assert.AreEqual("OLD=1\n", rig.ProjectStore.Items["p3"].Server.EnvFile, "a project on another server is untouched");
    }

    [TestMethod]
    public async Task UpdatingServerVariables_KeepsEveryOtherFieldOnTheProject()
    {
        var rig = new Rig();
        var before = rig.ProjectStore.Items["p1"];
        var updated = rig.ServerStore.Items["s1"] with { EnvFile = "NEW=2\n", Host = "9.9.9.9" };

        await ServerRouter.HandleUpdateAsync("s1", updated, rig.ServerStore, rig.ProjectStore);

        var after = rig.ProjectStore.Items["p1"];
        Assert.AreEqual("original-host", after.Server.Host, "only the variables are copied across");
        Assert.AreEqual("/deploy/p1", after.Server.RemoteWorkingDir);
        Assert.AreEqual(before.Name, after.Name);
        Assert.AreEqual("NEW=2\n", after.Server.EnvFile);
    }

    [TestMethod]
    public async Task UpdatingAnUnknownServer_IsNotFoundAndChangesNothing()
    {
        var rig = new Rig();
        var r = await ServerRouter.HandleUpdateAsync("missing", new ServerConfig { Id = "missing", EnvFile = "X=1" }, rig.ServerStore, rig.ProjectStore);

        Assert.AreEqual(404, Status(r));
        Assert.AreEqual("OLD=1\n", rig.ProjectStore.Items["p1"].Server.EnvFile);
        Assert.AreEqual(3, rig.ProjectStore.Count);
    }
}
