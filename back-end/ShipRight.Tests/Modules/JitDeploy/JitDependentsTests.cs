using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Builds;
using ShipRight.Modules.JitDeploy;
using ShipRight.Modules.Projects;
using ShipRight.Shared.Events;

namespace ShipRight.Tests.Modules.JitDeploy;

/// <summary>
/// "When api is replaced, also restart proxy?" is the user's call, case by case. These work out which questions there
/// are to ask: only about services the deploy does not build itself, and only down the chain the user keeps saying yes to.
/// </summary>
[TestClass]
public class JitDependentsTests
{
    private static IReadOnlyDictionary<string, IReadOnlyList<string>> G(params (string Svc, string[] Deps)[] e) =>
        e.ToDictionary(x => x.Svc, x => (IReadOnlyList<string>)x.Deps);

    // db <- api <- web <- proxy <- cdn ; both api and web are built by the deploy
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> Stack =
        G(("db", []), ("api", ["db"]), ("web", ["api"]), ("proxy", ["web"]), ("cdn", ["proxy"]));

    private static string[] Show(IEnumerable<DependentPair> p) => p.Select(x => $"{x.Service}>{x.Dependent}:{(x.Restart ? "restart" : "leave")}").ToArray();

    [TestMethod]
    public void OnlyServicesTheDeployDoesNotBuild_AreAsked_AndTheChainFollowsYesAnswers()
    {
        var pairs = DependentPairs.Compute(Stack, ["api", "web"], null);
        CollectionAssert.AreEqual(new[] { "proxy>cdn:restart", "web>proxy:restart" }, Show(pairs));
    }

    [TestMethod]
    public void ALeaveRunningAnswer_EndsTheChain_SoThereIsNothingFurtherToAsk()
    {
        var choices = new Dictionary<(string, string), bool> { [("web", "proxy")] = false };
        CollectionAssert.AreEqual(new[] { "web>proxy:leave" }, Show(DependentPairs.Compute(Stack, ["api", "web"], choices)));
    }

    [TestMethod]
    public void ServicesTheDeployBuildsItself_AreNeverAsked()
    {
        // web is not built when only api is: it is a dependent worth asking about
        CollectionAssert.AreEqual(new[] { "api>web:restart", "proxy>cdn:restart", "web>proxy:restart" }, Show(DependentPairs.Compute(Stack, ["api"], null)));
        // but once every dependent is built by the deploy itself there is nothing to ask
        Assert.AreEqual(0, DependentPairs.Compute(Stack, ["api", "web", "proxy", "cdn"], null).Count);
    }

    [TestMethod]
    public void AServiceWithNothingDependingOnIt_HasNothingToAsk()
    {
        Assert.AreEqual(0, DependentPairs.Compute(Stack, ["cdn"], null).Count);
        Assert.AreEqual(0, DependentPairs.Compute(G(), [], null).Count);
    }

    [TestMethod]
    public void TheListIsInAStableOrder()
    {
        var g = G(("a", []), ("z", ["a"]), ("m", ["a"]));
        CollectionAssert.AreEqual(new[] { "a>m:restart", "a>z:restart" }, Show(DependentPairs.Compute(g, ["a"], null)));
    }

    [TestMethod]
    public void ADependencyNamedButNotDefined_IsIgnored_NotACrash()
    {
        var g = G(("web", ["ghost"]), ("api", []));
        Assert.AreEqual(0, DependentPairs.Compute(g, ["api"], null).Count);
    }

    // ── from the server ──────────────────────────────────────────────────────

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

    private sealed class Builds : IBuildStore
    {
        public int Count => 0;
        public Task SaveAsync(BuildRecord r) => Task.CompletedTask;
        public Task<BuildRecord?> GetByIdAsync(string id) => Task.FromResult<BuildRecord?>(null);
        public Task<List<BuildRecord>> QueryAsync(string? projectId, string? status, DateTime? from, DateTime? to, string? gitTag, int page, int pageSize) => Task.FromResult(new List<BuildRecord>());
        public Task<int> CountQueryAsync(string? projectId, string? status, DateTime? from, DateTime? to, string? gitTag) => Task.FromResult(0);
        public Task MarkInterruptedAsync() => Task.CompletedTask;
    }

    private sealed class Configs : IJitConfigStore
    {
        public Dictionary<string, JitDeployConfig> Items { get; } = [];
        public Task<JitDeployConfig?> GetAsync(string projectId) => Task.FromResult(Items.GetValueOrDefault(projectId));
        public Task SaveAsync(string projectId, JitDeployConfig config) { Items[projectId] = config; return Task.CompletedTask; }
        public Task DeleteAsync(string projectId) { Items.Remove(projectId); return Task.CompletedTask; }
    }

    private static (JitDeployService Svc, FakeRemote Remote, Configs Configs) Rig(bool withProxy = true)
    {
        var projects = new Projects();
        projects.Items["p1"] = new ProjectConfig
        {
            Id = "p1", Name = "Shop",
            Services =
            [
                new ServiceConfig { Name = "api", DockerImageName = "shop/api", ComposeServiceName = "api" },
                new ServiceConfig { Name = "web", DockerImageName = "shop/web", ComposeServiceName = "web" },
            ],
            Server = new ServerConfig { Host = "10.0.0.5", Username = "deploy", SshKeyPath = "/k", RemoteWorkingDir = "/home/deploy/shop" },
        };
        var configs = new Configs();
        configs.Items["p1"] = new JitDeployConfig { Enabled = true, RepoUrl = "git@h:o/r.git", ComposeProject = "shop" };
        var remote = new FakeRemote();
        if (withProxy) remote.Stack.Add(new("proxy", ["web"], false, "nginx:1", null));
        return (new JitDeployService(projects, configs, new Builds(), new BuildEventBus(), remote, new FakeClock(), new JitDeployLock()), remote, configs);
    }

    [TestMethod]
    public async Task TheServer_IsAskedForItsComposeFile_AndTheQuestionsComeBack()
    {
        var (svc, _, _) = Rig();
        var v = await svc.GetDependentsAsync("p1");

        Assert.IsNull(v.Note);
        CollectionAssert.AreEqual(new[] { "web>proxy:restart" }, Show(v.Pairs));
    }

    [TestMethod]
    public async Task SavedAnswers_ArePutOverTheDefaults()
    {
        var (svc, _, configs) = Rig();
        configs.Items["p1"] = configs.Items["p1"] with { DependentChoices = [new JitDependentChoice("web", "proxy", false)] };
        var v = await svc.GetDependentsAsync("p1");
        CollectionAssert.AreEqual(new[] { "web>proxy:leave" }, Show(v.Pairs));
    }

    [TestMethod]
    public async Task AStackWithNothingExtra_HasNoQuestions()
    {
        var (svc, _, _) = Rig(withProxy: false);
        var v = await svc.GetDependentsAsync("p1");
        Assert.AreEqual(0, v.Pairs.Count);
        Assert.IsNull(v.Note);
    }

    [TestMethod]
    public async Task BeforeTheFirstDeploy_ThereIsNoComposeFileToRead_AndItSaysSo()
    {
        var (svc, remote, _) = Rig();
        remote.ConfigFails = true;
        var v = await svc.GetDependentsAsync("p1");
        Assert.AreEqual(0, v.Pairs.Count);
        StringAssert.Contains(v.Note, "Deploy once");
    }

    [TestMethod]
    public async Task AServerThatCannotBeReached_SaysSo_InsteadOfThrowing()
    {
        var (svc, remote, _) = Rig();
        remote.BeforeCommand = _ => throw new IOException("no route to host");
        var v = await svc.GetDependentsAsync("p1");
        StringAssert.Contains(v.Note, "no route to host");
    }

    [TestMethod]
    public async Task ItNeverChangesAnythingOnTheServer()
    {
        var (svc, remote, _) = Rig();
        await svc.GetDependentsAsync("p1");
        Assert.IsFalse(remote.Commands.Any(c => c.Contains(" stop ") || c.Contains(" up ") || c.Contains(" build ") || c.Contains("base64 -d") || c.Contains("checkout")));
        Assert.AreEqual(0, remote.Writes.Count);
    }

    [TestMethod]
    public async Task TheRoute_ReturnsTheView_AndNotFoundForAnUnknownProject()
    {
        var (svc, _, _) = Rig();
        var ok = await JitDeployRoutes.HandleDependentsAsync("p1", svc);
        Assert.AreEqual(200, ((IStatusCodeHttpResult)ok).StatusCode);
        Assert.IsInstanceOfType(((IValueHttpResult)ok).Value, typeof(JitDependentsView));

        var missing = await JitDeployRoutes.HandleDependentsAsync("nope", svc);
        Assert.AreEqual(404, ((IStatusCodeHttpResult)missing).StatusCode);
    }
}
