using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.BootstrapDeployment;
using ShipRight.Modules.JitDeploy;
using ShipRight.Modules.Projects;
using ShipRight.Modules.Servers;

namespace ShipRight.Tests.Modules.BootstrapDeployment;

[TestClass]
public class BootstrapDeploymentRoutesTests
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
        public Task<BootstrapKey> EnsureAsync(string serverId, CancellationToken ct) =>
            Task.FromResult(new BootstrapKey("/home/me/.ssh/server-" + serverId + ".pem", "ssh-ed25519 AAAA test", true));
    }

    private static (int Status, JsonElement Body) Run(IResult result)
    {
        var ctx = new DefaultHttpContext();
        ctx.RequestServices = new ServiceCollection().AddLogging().AddProblemDetails().BuildServiceProvider();
        ctx.Response.Body = new MemoryStream();
        result.ExecuteAsync(ctx).GetAwaiter().GetResult();
        ctx.Response.Body.Position = 0;
        var text = new StreamReader(ctx.Response.Body).ReadToEnd();
        return (ctx.Response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
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

    private static BootstrapApplier Applier(out FakeProjects projects, out FakeServers servers, out FakeJit jit)
    {
        projects = new FakeProjects();
        servers = new FakeServers();
        jit = new FakeJit();
        return new BootstrapApplier(projects, servers, jit, new FakeKeys());
    }

    [TestMethod]
    public void Plan_ReturnsTheWholeConfigurationAndSaysItIsOk()
    {
        var (status, body) = Run(BootstrapDeploymentRoutes.HandlePlan(Request()));

        Assert.AreEqual(200, status);
        Assert.IsTrue(body.GetProperty("ok").GetBoolean());
        Assert.AreEqual("Shop", body.GetProperty("project").GetProperty("name").GetString());
        Assert.IsTrue(body.GetProperty("jit").GetProperty("enabled").GetBoolean());
        Assert.AreEqual(1, body.GetProperty("jit").GetProperty("composeServices").GetArrayLength());
    }

    [TestMethod]
    public void Plan_WithProblems_StillReturnsThePlanSoTheScreenCanExplainWhy()
    {
        var (status, body) = Run(BootstrapDeploymentRoutes.HandlePlan(Request() with { Host = "", RepoUrl = "" }));

        Assert.AreEqual(200, status);
        Assert.IsFalse(body.GetProperty("ok").GetBoolean());
        Assert.IsTrue(body.GetProperty("problems").GetArrayLength() > 0);
    }

    [TestMethod]
    public void Apply_WritesTheConfigurationAndReturnsItsIds()
    {
        var applier = Applier(out var projects, out var servers, out var jit);

        var (status, body) = Run(BootstrapDeploymentRoutes.HandleApplyAsync(Request(), applier).GetAwaiter().GetResult());

        Assert.AreEqual(200, status);
        Assert.IsTrue(body.GetProperty("result").GetProperty("ok").GetBoolean());
        var projectId = body.GetProperty("result").GetProperty("projectId").GetString();
        Assert.IsFalse(string.IsNullOrWhiteSpace(projectId));
        Assert.IsTrue(projects.Items.ContainsKey(projectId!));
        Assert.AreEqual(1, servers.Items.Count);
        Assert.AreEqual(1, jit.Items.Count);
    }

    [TestMethod]
    public void Apply_WithProblems_IsABadRequestAndWritesNothing()
    {
        var applier = Applier(out var projects, out var servers, out var jit);

        var (status, body) = Run(BootstrapDeploymentRoutes.HandleApplyAsync(
            Request() with { Host = "" }, applier).GetAwaiter().GetResult());

        Assert.AreEqual(400, status);
        Assert.IsTrue(body.GetProperty("isError").GetBoolean());
        Assert.AreEqual(0, projects.Items.Count);
        Assert.AreEqual(0, servers.Items.Count);
        Assert.AreEqual(0, jit.Items.Count);
    }
}
