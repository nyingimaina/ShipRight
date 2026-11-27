using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.BootstrapDeployment;
using ShipRight.Modules.JitDeploy;
using ShipRight.Modules.Projects;
using ShipRight.Modules.Servers;

namespace ShipRight.Tests.Modules.BootstrapDeployment;

[TestClass]
public class BootstrapApplierTests
{
    private sealed class FakeProjects : IProjectStore
    {
        public Dictionary<string, ProjectConfig> Items { get; } = new(StringComparer.Ordinal);
        public int Count => Items.Count;
        public Task<List<ProjectConfig>> GetAllAsync() => Task.FromResult(Items.Values.ToList());
        public Task<ProjectConfig?> GetByIdAsync(string id) => Task.FromResult(Items.GetValueOrDefault(id));
        public Task<ProjectConfig?> GetByNameAsync(string name) =>
            Task.FromResult(Items.Values.FirstOrDefault(p => p.Name == name));
        public Task SaveAsync(ProjectConfig project) { Items[project.Id] = project; return Task.CompletedTask; }
        public Task DeleteAsync(string id) { Items.Remove(id); return Task.CompletedTask; }
    }

    private sealed class FakeServers : IServerStore
    {
        public Dictionary<string, ServerConfig> Items { get; } = new(StringComparer.Ordinal);
        public Task<List<ServerConfig>> GetAllAsync() => Task.FromResult(Items.Values.ToList());
        public Task<ServerConfig?> GetByIdAsync(string id) => Task.FromResult(Items.GetValueOrDefault(id));
        public Task SaveAsync(ServerConfig server) { Items[server.Id] = server; return Task.CompletedTask; }
        public Task DeleteAsync(string id) { Items.Remove(id); return Task.CompletedTask; }
    }

    private sealed class FakeJit : IJitConfigStore
    {
        public Dictionary<string, JitDeployConfig> Items { get; } = new(StringComparer.Ordinal);
        public Task<JitDeployConfig?> GetAsync(string projectId) => Task.FromResult(Items.GetValueOrDefault(projectId));
        public Task SaveAsync(string projectId, JitDeployConfig config) { Items[projectId] = config; return Task.CompletedTask; }
        public Task DeleteAsync(string projectId) { Items.Remove(projectId); return Task.CompletedTask; }
    }

    private sealed class FakeKeys : IBootstrapKeyProvisioner
    {
        public int Calls { get; private set; }
        public string PublicKey { get; set; } = "ssh-ed25519 AAAA test";
        public string PrivateKeyPath { get; set; } = "/home/me/.ssh/server-new.pem";
        public Task<BootstrapKey> EnsureAsync(string serverId, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new BootstrapKey(PrivateKeyPath, PublicKey, true));
        }
    }

    private static BootstrapRequest Request() => new()
    {
        ProjectName = "Shop",
        RepoUrl = "git@github.com:acme/shop.git",
        ComposeFile = "docker-compose.yml",
        Services = [new BootstrapService { Name = "api", Image = "acme/shop-api", Dockerfile = "Dockerfile" }],
        ServerName = "staging",
        Host = "staging.example.com",
        Username = "deploy",
        RemoteWorkingDir = "/home/deploy/shop",
    };

    private static (BootstrapApplier Applier, FakeProjects Projects, FakeServers Servers, FakeJit Jit, FakeKeys Keys) Create()
    {
        var projects = new FakeProjects();
        var servers = new FakeServers();
        var jit = new FakeJit();
        var keys = new FakeKeys();
        return (new BootstrapApplier(projects, servers, jit, keys), projects, servers, jit, keys);
    }

    [TestMethod]
    public async Task Apply_WritesTheServerProjectAndJitConfig()
    {
        var (applier, projects, servers, jit, _) = Create();
        var plan = BootstrapPlanner.Plan(Request());

        var result = await applier.ApplyAsync(plan);

        Assert.IsTrue(result.Ok, string.Join(" | ", result.Problems));
        Assert.IsNotNull(result.ProjectId);
        Assert.IsNotNull(result.ServerId);
        Assert.IsTrue(servers.Items.ContainsKey(result.ServerId!));
        Assert.IsTrue(projects.Items.ContainsKey(result.ProjectId!));
        Assert.IsTrue(jit.Items.ContainsKey(result.ProjectId!));
        Assert.AreEqual(result.ServerId, projects.Items[result.ProjectId!].ServerId);
        Assert.AreEqual(result.ServerId, projects.Items[result.ProjectId!].Server.Id);
        Assert.IsTrue(jit.Items[result.ProjectId!].Enabled);
    }

    [TestMethod]
    public async Task Apply_ReusesAnExistingServerWithTheSameAddress()
    {
        var (applier, _, servers, _, _) = Create();
        servers.Items["srv-1"] = new ServerConfig { Id = "srv-1", Name = "old", Host = "staging.example.com", SshKeyPath = "/keys/old.pem" };
        var plan = BootstrapPlanner.Plan(Request() with { SshKeyPath = "/keys/old.pem" });

        var result = await applier.ApplyAsync(plan);

        Assert.IsTrue(result.Ok, string.Join(" | ", result.Problems));
        Assert.AreEqual("srv-1", result.ServerId);
        Assert.AreEqual(1, servers.Items.Count);
    }

    [TestMethod]
    public async Task Apply_ReusesAnExistingProjectWithTheSameName()
    {
        var (applier, projects, _, _, _) = Create();
        projects.Items["shop"] = new ProjectConfig { Id = "shop", Name = "Shop", CreatedAt = new DateTime(2020, 1, 1) };
        var plan = BootstrapPlanner.Plan(Request());

        var result = await applier.ApplyAsync(plan);

        Assert.IsTrue(result.Ok, string.Join(" | ", result.Problems));
        Assert.AreEqual("shop", result.ProjectId);
        Assert.AreEqual(1, projects.Items.Count);
        Assert.AreEqual(new DateTime(2020, 1, 1), projects.Items["shop"].CreatedAt, "The original creation date is kept.");
    }

    [TestMethod]
    public async Task Apply_RefusesAnIncompletePlanAndWritesNothing()
    {
        var (applier, projects, servers, jit, _) = Create();
        var plan = BootstrapPlanner.Plan(Request() with { Host = "" });

        var result = await applier.ApplyAsync(plan);

        Assert.IsFalse(result.Ok);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.Problems.FirstOrDefault()));
        Assert.AreEqual(0, projects.Items.Count);
        Assert.AreEqual(0, servers.Items.Count);
        Assert.AreEqual(0, jit.Items.Count);
    }

    [TestMethod]
    public async Task Apply_GeneratesAManagedKeyWhenThePlanHasNoKeyPath()
    {
        var (applier, _, servers, _, keys) = Create();
        var plan = BootstrapPlanner.Plan(Request() with { SshKeyPath = "" });

        var result = await applier.ApplyAsync(plan);

        Assert.IsTrue(result.Ok, string.Join(" | ", result.Problems));
        Assert.AreEqual(1, keys.Calls);
        Assert.AreEqual(keys.PublicKey, result.PublicKey);
        var saved = servers.Items[result.ServerId!];
        Assert.AreEqual(keys.PrivateKeyPath, saved.SshKeyPath);
        Assert.IsTrue(saved.ManagedSshKey);
        Assert.IsFalse(saved.ManagedSshKeyAuthorized);
    }

    [TestMethod]
    public async Task Apply_DoesNotGenerateAKeyWhenAPathWasGiven()
    {
        var (applier, _, _, _, keys) = Create();
        var plan = BootstrapPlanner.Plan(Request() with { SshKeyPath = "/keys/staging.pem" });

        var result = await applier.ApplyAsync(plan);

        Assert.IsTrue(result.Ok, string.Join(" | ", result.Problems));
        Assert.AreEqual(0, keys.Calls);
        Assert.IsNull(result.PublicKey);
    }

    [TestMethod]
    public async Task Apply_WithOnlyAnAddress_SavesTheServerWithoutMakingAKey()
    {
        var (applier, projects, servers, jit, keys) = Create();
        var plan = BootstrapPlanner.Plan(ShipRightSelfTemplate.Create(
            host: "203.0.113.10",
            username: "",
            remoteWorkingDir: "",
            domain: ""));

        var result = await applier.ApplyAsync(plan);

        Assert.IsTrue(result.Ok, string.Join(" | ", result.Problems));
        Assert.AreEqual(0, keys.Calls);
        var saved = servers.Items[result.ServerId!];
        Assert.AreEqual("203.0.113.10", saved.Host);
        Assert.AreEqual("", saved.Username);
        Assert.AreEqual("", saved.SshKeyPath);
        Assert.IsTrue(projects.Items.ContainsKey(result.ProjectId!));
        Assert.IsTrue(jit.Items.ContainsKey(result.ProjectId!));
    }

    [TestMethod]
    public async Task Apply_ReportsAnActionableErrorWhenTheKeyCannotBeMade()
    {
        var (applier, projects, servers, jit, _) = Create();
        var failing = new BootstrapApplier(
            new FakeProjects(), new FakeServers(), new FakeJit(),
            new ThrowingKeys());
        var plan = BootstrapPlanner.Plan(Request() with { SshKeyPath = "" });

        var result = await failing.ApplyAsync(plan);

        Assert.IsFalse(result.Ok);
        StringAssert.Contains(string.Join(" ", result.Problems).ToLowerInvariant(), "ssh-keygen");
    }

    [TestMethod]
    public async Task Apply_ProducesAProjectTheDeployMapperAccepts()
    {
        var (applier, projects, _, jit, _) = Create();
        var plan = BootstrapPlanner.Plan(Request() with { SshKeyPath = "/keys/staging.pem" });

        var result = await applier.ApplyAsync(plan);
        var mapped = JitDeployMapper.Map(projects.Items[result.ProjectId!], jit.Items[result.ProjectId!]);

        Assert.IsTrue(result.Ok, string.Join(" | ", result.Problems));
        Assert.IsTrue(mapped.Ok, string.Join(" | ", mapped.Problems));
        Assert.AreEqual("shop", mapped.Request!.Project);
        Assert.AreEqual("api", mapped.Request.Services.Single().Name);
    }

    [TestMethod]
    public async Task Apply_OfTheSelfPreset_ProducesAProjectTheDeployMapperAccepts()
    {
        var (applier, projects, _, jit, _) = Create();
        var plan = BootstrapPlanner.Plan(ShipRightSelfTemplate.Create(
            host: "staging.example.com",
            username: "deploy",
            remoteWorkingDir: "/home/deploy/shipright",
            domain: "staging.shipright.example.com",
            sshKeyPath: "/keys/staging.pem"));

        var result = await applier.ApplyAsync(plan);
        var mapped = JitDeployMapper.Map(projects.Items[result.ProjectId!], jit.Items[result.ProjectId!]);

        Assert.IsTrue(result.Ok, string.Join(" | ", result.Problems));
        Assert.IsTrue(mapped.Ok, string.Join(" | ", mapped.Problems));
        Assert.AreEqual("shipright", mapped.Request!.Project);
        Assert.AreEqual("app", mapped.Request.Services.Single().Name);
    }

    private sealed class ThrowingKeys : IBootstrapKeyProvisioner
    {
        public Task<BootstrapKey> EnsureAsync(string serverId, CancellationToken ct) =>
            throw new InvalidOperationException("ssh-keygen was not found on this computer.");
    }
}
