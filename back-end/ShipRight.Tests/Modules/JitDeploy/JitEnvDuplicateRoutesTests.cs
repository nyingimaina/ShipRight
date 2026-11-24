using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.JitDeploy;
using ShipRight.Modules.Projects;
using ShipRight.Modules.Servers;

namespace ShipRight.Tests.Modules.JitDeploy;

/// <summary>
/// Deploy-time duplicate check: it shows both values when asked, and removing from one side saves only that side.
/// The deploy log's names-only guarantee is unchanged.
/// </summary>
[TestClass]
public class JitEnvDuplicateRoutesTests
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

    private sealed class Configs : IJitConfigStore
    {
        public Dictionary<string, JitDeployConfig> Items { get; } = [];
        public Task<JitDeployConfig?> GetAsync(string id) => Task.FromResult(Items.GetValueOrDefault(id));
        public Task SaveAsync(string id, JitDeployConfig c) { Items[id] = c; return Task.CompletedTask; }
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
        public Configs ConfigStore { get; } = new();
        public Servers ServerStore { get; } = new();

        public Rig(string serverEnv = "DB=server-db\nKEEP=1\n", string projectEnv = "DB=project-db\nNEW=x\n", string envFilePath = "")
        {
            ProjectStore.Items["p1"] = new ProjectConfig
            {
                Id = "p1", Name = "Shop",
                Server = new ServerConfig { Id = "s1", Host = "h", Username = "u", SshKeyPath = "/k", RemoteWorkingDir = "/deploy/shop", EnvFile = serverEnv },
            };
            ConfigStore.Items["p1"] = new JitDeployConfig { Enabled = true, RepoUrl = "git@h:o/r.git", Branch = "main", EnvText = projectEnv, EnvFile = envFilePath };
            ServerStore.Items["s1"] = ProjectStore.Items["p1"].Server;
        }
    }

    private static int Status(IResult r) => ((IStatusCodeHttpResult)r).StatusCode ?? 0;
    private static object? Value(IResult r) => ((IValueHttpResult)r).Value;
    private static string Json(object? value) => System.Text.Json.JsonSerializer.Serialize(value, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

    [TestMethod]
    public async Task Route_GetDuplicates_ShowsTheNameAndBothValues()
    {
        var rig = new Rig();
        var r = await JitEnvDuplicateRoutes.HandleGetDuplicatesAsync("p1", rig.ProjectStore, rig.ConfigStore);
        var json = Json(Value(r));

        Assert.AreEqual(200, Status(r));
        StringAssert.Contains(json, "\"name\":\"DB\"");
        StringAssert.Contains(json, "server-db");
        StringAssert.Contains(json, "project-db");
        Assert.IsFalse(json.Contains("KEEP"));
        Assert.IsFalse(json.Contains("NEW"));
    }

    [TestMethod]
    public async Task Route_GetDuplicates_WhenTheProjectKeepsItsOwnEnvFile_IsSkipped()
    {
        var rig = new Rig(envFilePath: "/etc/shop/.env");
        var r = await JitEnvDuplicateRoutes.HandleGetDuplicatesAsync("p1", rig.ProjectStore, rig.ConfigStore);
        var json = Json(Value(r));

        Assert.AreEqual(200, Status(r));
        StringAssert.Contains(json, "\"skipped\":true");
        Assert.IsFalse(json.Contains("server-db"));
    }

    [TestMethod]
    public async Task Route_GetDuplicates_ForAnUnknownProject_IsNotFound()
    {
        var rig = new Rig();
        Assert.AreEqual(404, Status(await JitEnvDuplicateRoutes.HandleGetDuplicatesAsync("nope", rig.ProjectStore, rig.ConfigStore)));
    }

    [TestMethod]
    public async Task Route_RemoveFromProject_KeepsTheServerTextUntouched()
    {
        var rig = new Rig();
        var r = await JitEnvDuplicateRoutes.HandleRemoveAsync("p1", new EnvDuplicateRemoval { RemoveFrom = "project", Names = ["DB"] }, rig.ProjectStore, rig.ConfigStore, rig.ServerStore);

        Assert.AreEqual(200, Status(r));
        Assert.AreEqual("NEW=x\n", rig.ConfigStore.Items["p1"].EnvText);
        Assert.AreEqual("DB=server-db\nKEEP=1\n", rig.ProjectStore.Items["p1"].Server.EnvFile);
        Assert.AreEqual("DB=server-db\nKEEP=1\n", rig.ServerStore.Items["s1"].EnvFile);
        Assert.IsFalse(Json(Value(r)).Contains("\"name\":\"DB\""));
    }

    [TestMethod]
    public async Task Route_RemoveFromServer_UpdatesBothTheProjectsServerAndTheServerRecord()
    {
        var rig = new Rig();
        var r = await JitEnvDuplicateRoutes.HandleRemoveAsync("p1", new EnvDuplicateRemoval { RemoveFrom = "server", Names = ["DB"] }, rig.ProjectStore, rig.ConfigStore, rig.ServerStore);

        Assert.AreEqual(200, Status(r));
        Assert.AreEqual("KEEP=1\n", rig.ProjectStore.Items["p1"].Server.EnvFile);
        Assert.AreEqual("KEEP=1\n", rig.ServerStore.Items["s1"].EnvFile);
        Assert.AreEqual("DB=project-db\nNEW=x\n", rig.ConfigStore.Items["p1"].EnvText);
    }

    [TestMethod]
    public async Task Route_Remove_WithAnUnknownName_ChangesNothingAndSaysWhy()
    {
        var rig = new Rig();
        var r = await JitEnvDuplicateRoutes.HandleRemoveAsync("p1", new EnvDuplicateRemoval { RemoveFrom = "server", Names = ["KEEP"] }, rig.ProjectStore, rig.ConfigStore, rig.ServerStore);

        Assert.AreEqual(400, Status(r));
        Assert.AreEqual("DB=server-db\nKEEP=1\n", rig.ProjectStore.Items["p1"].Server.EnvFile);
        Assert.AreEqual("DB=project-db\nNEW=x\n", rig.ConfigStore.Items["p1"].EnvText);
    }

    [TestMethod]
    public async Task Route_Remove_KeepBoth_ReportsTheDuplicatesAndSavesNothing()
    {
        var rig = new Rig();
        var beforeProject = rig.ProjectStore.Items["p1"];
        var beforeConfig = rig.ConfigStore.Items["p1"];
        var r = await JitEnvDuplicateRoutes.HandleRemoveAsync("p1", new EnvDuplicateRemoval { RemoveFrom = "none" }, rig.ProjectStore, rig.ConfigStore, rig.ServerStore);

        Assert.AreEqual(200, Status(r));
        Assert.AreSame(beforeProject, rig.ProjectStore.Items["p1"]);
        Assert.AreSame(beforeConfig, rig.ConfigStore.Items["p1"]);
        StringAssert.Contains(Json(Value(r)), "\"name\":\"DB\"");
    }
}
