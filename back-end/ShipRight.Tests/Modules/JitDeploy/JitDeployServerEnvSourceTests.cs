using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Builds;
using ShipRight.Modules.JitDeploy;
using ShipRight.Modules.Projects;
using ShipRight.Modules.Servers;
using ShipRight.Shared.Events;

namespace ShipRight.Tests.Modules.JitDeploy;

/// <summary>
/// Server-level variables belong to the server record. A deploy must merge that record with the project's variables,
/// never the stale copy embedded in the project.
/// </summary>
[TestClass]
public class JitDeployServerEnvSourceTests
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

    private sealed class Builds : IBuildStore
    {
        public Dictionary<string, BuildRecord> Items { get; } = [];
        public int Count => Items.Count;
        public Task SaveAsync(BuildRecord r) { Items[r.Id] = r; return Task.CompletedTask; }
        public Task<BuildRecord?> GetByIdAsync(string id) => Task.FromResult(Items.GetValueOrDefault(id));
        public Task<List<BuildRecord>> QueryAsync(string? projectId, string? status, DateTime? from, DateTime? to, string? gitTag, int page, int pageSize) => Task.FromResult(new List<BuildRecord>());
        public Task<int> CountQueryAsync(string? projectId, string? status, DateTime? from, DateTime? to, string? gitTag) => Task.FromResult(0);
        public Task MarkInterruptedAsync() => Task.CompletedTask;
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
        public Builds BuildStore { get; } = new();
        public Servers ServerStore { get; } = new();
        public FakeRemote Remote { get; } = new();
        public JitDeployService Service { get; }

        public Rig(string projectEnv = "DB=stale\n", string recordEnv = "DB=authoritative\nKEEP=1\n", bool withServerRecord = true)
        {
            ProjectStore.Items["p1"] = new ProjectConfig
            {
                Id = "p1", Name = "Shop", ServerId = "s1",
                Services = [new ServiceConfig { Name = "api", DockerImageName = "shop/api", ComposeServiceName = "api" }],
                Server = new ServerConfig { Host = "10.0.0.5", Username = "deploy", SshKeyPath = "/keys/k", RemoteWorkingDir = "/home/deploy/shop", EnvFile = projectEnv },
            };
            ConfigStore.Items["p1"] = new JitDeployConfig { Enabled = true, RepoUrl = "git@github.com:o/r.git", ComposeProject = "shop" };
            if (withServerRecord)
                ServerStore.Items["s1"] = new ServerConfig { Id = "s1", Host = "10.0.0.5", Username = "deploy", EnvFile = recordEnv };
            Service = new JitDeployService(ProjectStore, ConfigStore, BuildStore, new BuildEventBus(), Remote, new FakeClock(),
                new JitDeployLock(), null, null, ServerStore);
        }
    }

    [TestMethod]
    public void Mapper_MergesTheSuppliedServerRecordVarsAndDescribesThem()
    {
        var project = new ProjectConfig
        {
            Id = "p1",
            Services = [new ServiceConfig { Name = "api", DockerImageName = "shop/api", ComposeServiceName = "api" }],
            Server = new ServerConfig { Host = "10.0.0.5", Username = "deploy", SshKeyPath = "/keys/k", RemoteWorkingDir = "/home/deploy/shop", EnvFile = "DB=stale\n" },
        };
        var cfg = new JitDeployConfig { Enabled = true, RepoUrl = "git@h:o/r.git", Branch = "main", ComposeProject = "shop", EnvText = "NEW=x\n" };

        var r = JitEnvMapper.MapWithServerEnv(project, cfg, "DB=authoritative\nKEEP=1\n");

        Assert.IsTrue(r.Ok);
        StringAssert.Contains(r.Request!.EnvContent!, "DB=authoritative");
        StringAssert.Contains(r.Request.EnvContent!, "NEW=x");
        Assert.IsFalse(r.Request.EnvContent!.Contains("DB=stale"));
        StringAssert.Contains(r.Request.EnvNote!, "2 from the server settings");
    }

    [TestMethod]
    public async Task Deploy_UsesTheServerRecordVars_NotTheProjectsStaleCopy()
    {
        var rig = new Rig();
        var start = await rig.Service.StartDeployAsync("p1", null, "tester");

        Assert.IsNull(start.Error, start.Error);
        await start.Completion!;
        StringAssert.Contains(rig.Remote.EnvContent!, "DB=authoritative");
        StringAssert.Contains(rig.Remote.EnvContent!, "KEEP=1");
        Assert.IsFalse(rig.Remote.EnvContent!.Contains("DB=stale"));
    }

    [TestMethod]
    public async Task Deploy_WhenThereIsNoServerRecord_FallsBackToTheProjectsCopy()
    {
        var rig = new Rig(withServerRecord: false);
        var start = await rig.Service.StartDeployAsync("p1", null, "tester");

        Assert.IsNull(start.Error, start.Error);
        await start.Completion!;
        StringAssert.Contains(rig.Remote.EnvContent!, "DB=stale");
    }
}
