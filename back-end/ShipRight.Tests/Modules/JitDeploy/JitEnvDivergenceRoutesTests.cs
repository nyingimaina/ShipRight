using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.JitDeploy;
using ShipRight.Modules.Projects;
using ShipRight.Modules.Servers;

namespace ShipRight.Tests.Modules.JitDeploy;

/// <summary>
/// The deploy-time divergence check. It only reads when asked, shows both values, and writes exactly one side when the
/// person picks a direction. "Keep both" is a no-op.
/// </summary>
[TestClass]
public class JitEnvDivergenceRoutesTests
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

        public Rig(string projectEnv = "DB=project-db\n", string serverRecordEnv = "DB=server-db\nKEEP=1\n", bool withServerRecord = true)
        {
            ProjectStore.Items["p1"] = new ProjectConfig
            {
                Id = "p1", Name = "Shop", ServerId = "s1",
                Server = new ServerConfig { Id = "s1", Host = "h", Username = "u", SshKeyPath = "/k", RemoteWorkingDir = "/deploy/shop", EnvFile = projectEnv },
            };
            if (withServerRecord)
                ServerStore.Items["s1"] = new ServerConfig { Id = "s1", Host = "h", Username = "u", EnvFile = serverRecordEnv };
        }
    }

    private static int Status(IResult r) => ((IStatusCodeHttpResult)r).StatusCode ?? 0;
    private static object? Value(IResult r) => ((IValueHttpResult)r).Value;
    private static string Json(object? value) => System.Text.Json.JsonSerializer.Serialize(value, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

    [TestMethod]
    public async Task Route_Get_ShowsBothSidesAndTheValuesThatDiffer()
    {
        var rig = new Rig();
        var r = await JitEnvDivergenceRoutes.HandleGetAsync("p1", rig.ProjectStore, rig.ServerStore);
        var json = Json(Value(r));

        Assert.AreEqual(200, Status(r));
        StringAssert.Contains(json, "\"differs\":true");
        StringAssert.Contains(json, "\"serverFound\":true");
        StringAssert.Contains(json, "\"name\":\"DB\"");
        StringAssert.Contains(json, "server-db");
        StringAssert.Contains(json, "project-db");
    }

    [TestMethod]
    public async Task Route_Get_WhenTheSidesMatch_ReportsNoDivergence()
    {
        var rig = new Rig(projectEnv: "A=1\n", serverRecordEnv: "A=1\n");
        var r = await JitEnvDivergenceRoutes.HandleGetAsync("p1", rig.ProjectStore, rig.ServerStore);

        Assert.AreEqual(200, Status(r));
        StringAssert.Contains(Json(Value(r)), "\"differs\":false");
    }

    [TestMethod]
    public async Task Route_Get_ForAnUnknownProject_IsNotFound()
    {
        var rig = new Rig();
        Assert.AreEqual(404, Status(await JitEnvDivergenceRoutes.HandleGetAsync("nope", rig.ProjectStore, rig.ServerStore)));
    }

    [TestMethod]
    public async Task Route_Resolve_ToProject_CopiesTheServerRecordIntoTheProjectOnly()
    {
        var rig = new Rig();
        var r = await JitEnvDivergenceRoutes.HandleResolveAsync("p1", new EnvDivergenceResolution { Copy = "to-project" }, rig.ProjectStore, rig.ServerStore);

        Assert.AreEqual(200, Status(r));
        Assert.AreEqual("DB=server-db\nKEEP=1\n", rig.ProjectStore.Items["p1"].Server.EnvFile);
        Assert.AreEqual("DB=server-db\nKEEP=1\n", rig.ServerStore.Items["s1"].EnvFile);
        StringAssert.Contains(Json(Value(r)), "\"differs\":false");
    }

    [TestMethod]
    public async Task Route_Resolve_ToServer_CopiesTheProjectIntoTheServerRecordOnly()
    {
        var rig = new Rig();
        var r = await JitEnvDivergenceRoutes.HandleResolveAsync("p1", new EnvDivergenceResolution { Copy = "to-server" }, rig.ProjectStore, rig.ServerStore);

        Assert.AreEqual(200, Status(r));
        Assert.AreEqual("DB=project-db\n", rig.ServerStore.Items["s1"].EnvFile);
        Assert.AreEqual("DB=project-db\n", rig.ProjectStore.Items["p1"].Server.EnvFile);
    }

    [TestMethod]
    public async Task Route_Resolve_ToServer_RefreshesOtherProjectsOnTheSameServer()
    {
        var rig = new Rig();
        rig.ProjectStore.Items["p2"] = new ProjectConfig
        {
            Id = "p2", Name = "Other", ServerId = "s1",
            Server = new ServerConfig { Id = "s1", EnvFile = "DB=server-db\nKEEP=1\n" },
        };

        await JitEnvDivergenceRoutes.HandleResolveAsync("p1", new EnvDivergenceResolution { Copy = "to-server" }, rig.ProjectStore, rig.ServerStore);

        Assert.AreEqual("DB=project-db\n", rig.ProjectStore.Items["p2"].Server.EnvFile,
            "every project pointing at the server must see the newly authoritative values");
    }

    [TestMethod]
    public async Task Route_Resolve_KeepBoth_SavesNothing()
    {
        var rig = new Rig();
        var beforeProject = rig.ProjectStore.Items["p1"];
        var beforeServer = rig.ServerStore.Items["s1"];
        var r = await JitEnvDivergenceRoutes.HandleResolveAsync("p1", new EnvDivergenceResolution { Copy = "none" }, rig.ProjectStore, rig.ServerStore);

        Assert.AreEqual(200, Status(r));
        Assert.AreSame(beforeProject, rig.ProjectStore.Items["p1"]);
        Assert.AreSame(beforeServer, rig.ServerStore.Items["s1"]);
        StringAssert.Contains(Json(Value(r)), "\"differs\":true");
    }

    [TestMethod]
    public async Task Route_Resolve_WithAnUnknownChoice_IsBadRequest()
    {
        var rig = new Rig();
        var r = await JitEnvDivergenceRoutes.HandleResolveAsync("p1", new EnvDivergenceResolution { Copy = "sideways" }, rig.ProjectStore, rig.ServerStore);

        Assert.AreEqual(400, Status(r));
        Assert.AreEqual("DB=project-db\n", rig.ProjectStore.Items["p1"].Server.EnvFile);
    }

    [TestMethod]
    public async Task Route_Resolve_WhenThereIsNoServerRecord_IsBadRequest()
    {
        var rig = new Rig(withServerRecord: false);
        var r = await JitEnvDivergenceRoutes.HandleGetAsync("p1", rig.ProjectStore, rig.ServerStore);
        StringAssert.Contains(Json(Value(r)), "\"serverFound\":false");

        var resolved = await JitEnvDivergenceRoutes.HandleResolveAsync("p1", new EnvDivergenceResolution { Copy = "to-project" }, rig.ProjectStore, rig.ServerStore);
        Assert.AreEqual(400, Status(resolved));
    }

    [TestMethod]
    public async Task Route_Resolve_ForAnUnknownProject_IsNotFound()
    {
        var rig = new Rig();
        Assert.AreEqual(404, Status(await JitEnvDivergenceRoutes.HandleResolveAsync("nope", new EnvDivergenceResolution(), rig.ProjectStore, rig.ServerStore)));
    }
}
