using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.JitDeploy;
using ShipRight.Modules.Projects;

namespace ShipRight.Tests.Modules.JitDeploy;

/// <summary>
/// "This project has no services to deploy" explains itself: why, and what to do, using the compose file ShipRight can find
/// in the project's own folder. The original wording of the problem is unchanged.
/// </summary>
[TestClass]
public class JitServiceHelpTests
{
    private string _repo = "";

    [TestInitialize] public void Init() { _repo = Path.Combine(Path.GetTempPath(), "jsh-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path.Combine(_repo, ".git")); }
    [TestCleanup] public void Done() { try { Directory.Delete(_repo, true); } catch { /* best effort */ } }

    private void Put(string rel, string content = "")
    {
        var p = Path.Combine(_repo, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
    }

    private ProjectConfig Project(string? repoPath = null, params ServiceConfig[] services) => new()
    {
        Id = "p1", Name = "Herd IQ",
        Services = services.ToList(),
        GitRepos = repoPath is null ? [] : [new GitConfig { RepoPath = repoPath }],
        Server = new ServerConfig { Host = "h", Username = "u", SshKeyPath = "/k", RemoteWorkingDir = "/home/u/herd" },
    };

    private static JitDeployConfig Cfg(string composeFile = "") =>
        new() { Enabled = true, RepoUrl = "git@h:o/r.git", Branch = "main", ComposeFile = composeFile };

    // ── the explanation and the steps ────────────────────────────────────────

    [TestMethod]
    public void ItIsOnlyOfferedForTheNoServicesProblem()
    {
        Assert.IsNull(JitServiceHelp.For(Project(), Cfg(), ["Enter the git address the server should fetch your code from."]));
        Assert.IsNotNull(JitServiceHelp.For(Project(), Cfg(), ["Choose the server.", JitServiceHelp.NoServices]));
    }

    [TestMethod]
    public void ItExplainsWhyInPlainWords()
    {
        var h = JitServiceHelp.For(Project(), Cfg(), [JitServiceHelp.NoServices])!;
        StringAssert.Contains(h.Explanation, "which parts of your compose file");
        StringAssert.Contains(h.Explanation, "project's own services");
        Assert.IsFalse(h.Explanation.Contains("shipright", StringComparison.OrdinalIgnoreCase) && h.Explanation.Contains("TagBinding"));
    }

    [TestMethod]
    public void WithNoComposeFileFoundLocally_TheStepsSayHowToPointAtOne()
    {
        var h = JitServiceHelp.For(Project(_repo), Cfg(), [JitServiceHelp.NoServices])!;
        Assert.IsNull(h.Found);
        var steps = string.Join("\n", h.Steps);
        StringAssert.Contains(steps, "Browse");
        StringAssert.Contains(steps, "top folder");
        StringAssert.Contains(steps, "Edit project");
    }

    [TestMethod]
    public void WithNoLocalFolderAtAll_TheSameStepsApply()
    {
        var h = JitServiceHelp.For(Project(), Cfg(), [JitServiceHelp.NoServices])!;
        Assert.IsNull(h.Found);
        StringAssert.Contains(string.Join("\n", h.Steps), "Browse");
    }

    // ── what it finds ────────────────────────────────────────────────────────

    [TestMethod]
    public void ACompose_InTheProjectsRepoRoot_IsReadAndItsServicesSuggested()
    {
        Put("docker-compose.yml", "services:\n  db:\n    image: mariadb:11\n  backend:\n    image: reg/herd-backend:${IMAGE_TAG:-latest}\n  frontend:\n    image: reg/herd-frontend:${IMAGE_TAG:-latest}\n");
        Put("backend.Dockerfile", "FROM x\n");
        Put("frontend.Dockerfile", "FROM x\n");
        var h = JitServiceHelp.For(Project(_repo), Cfg(), [JitServiceHelp.NoServices])!;

        Assert.IsNotNull(h.Found);
        Assert.AreEqual("docker-compose.yml", h.Found!.RelativePath);
        CollectionAssert.AreEqual(new[] { "backend", "frontend" }, h.Found.Services.Select(s => s.Name).ToArray());
        var steps = string.Join("\n", h.Steps);
        StringAssert.Contains(steps, "backend, frontend");
        StringAssert.Contains(steps, "Use these services");
        StringAssert.Contains(steps, "BACKEND_TAG");
    }

    [TestMethod]
    public void TheNamedComposeFile_IsTheOneRead()
    {
        Put("docker-compose.yml", "services:\n  other:\n    build: ./o\n");
        Put("deploy/docker-compose.yml", "services:\n  api:\n    build: ./api\n    image: shop/api\n");
        var h = JitServiceHelp.For(Project(_repo), Cfg("deploy/docker-compose.yml"), [JitServiceHelp.NoServices])!;
        Assert.AreEqual("deploy/docker-compose.yml", h.Found!.RelativePath);
        CollectionAssert.AreEqual(new[] { "api" }, h.Found.Services.Select(s => s.Name).ToArray());
    }

    [TestMethod]
    public void AComposeFileWithNothingToBuild_SaysWhatToAdd()
    {
        Put("docker-compose.yml", "services:\n  db:\n    image: mariadb:11\n");
        var h = JitServiceHelp.For(Project(_repo), Cfg(), [JitServiceHelp.NoServices])!;
        Assert.AreEqual(0, h.Found!.Services.Count);
        var steps = string.Join("\n", h.Steps);
        StringAssert.Contains(steps, "build:");
        StringAssert.Contains(steps, "Dockerfile");
    }

    [TestMethod]
    public void ANamedFileThatIsNotThere_IsSaidPlainly()
    {
        var h = JitServiceHelp.For(Project(_repo), Cfg("deploy/missing.yml"), [JitServiceHelp.NoServices])!;
        Assert.IsNull(h.Found);
        StringAssert.Contains(string.Join("\n", h.Steps), "deploy/missing.yml");
    }

    [TestMethod]
    public void ItNeverThrows_WhateverTheFolderLooksLike()
    {
        Assert.IsNotNull(JitServiceHelp.For(Project(Path.Combine(_repo, "does-not-exist")), Cfg(), [JitServiceHelp.NoServices]));
        Put("docker-compose.yml", "{{{ not yaml");
        Assert.IsNotNull(JitServiceHelp.For(Project(_repo), Cfg(), [JitServiceHelp.NoServices]));
    }

    // ── where it shows ───────────────────────────────────────────────────────

    private sealed class MemoryProjects : IProjectStore
    {
        public Dictionary<string, ProjectConfig> All { get; } = [];
        public int Count => All.Count;
        public Task<ProjectConfig?> GetByNameAsync(string name) => Task.FromResult(All.Values.FirstOrDefault(p => p.Name == name));
        public Task<List<ProjectConfig>> GetAllAsync() => Task.FromResult(All.Values.ToList());
        public Task<ProjectConfig?> GetByIdAsync(string id) => Task.FromResult(All.GetValueOrDefault(id));
        public Task SaveAsync(ProjectConfig p) { All[p.Id] = p; return Task.CompletedTask; }
        public Task DeleteAsync(string id) { All.Remove(id); return Task.CompletedTask; }
    }

    private sealed class MemoryConfigs : IJitConfigStore
    {
        public Dictionary<string, JitDeployConfig> All { get; } = [];
        public Task<JitDeployConfig?> GetAsync(string id) => Task.FromResult(All.GetValueOrDefault(id));
        public Task SaveAsync(string id, JitDeployConfig c) { All[id] = c; return Task.CompletedTask; }
        public Task DeleteAsync(string id) { All.Remove(id); return Task.CompletedTask; }
    }

    private static string Json(IResult r) => System.Text.Json.JsonSerializer.Serialize(((IValueHttpResult)r).Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    [TestMethod]
    public async Task TheSettingsScreen_GetsTheHelp_WithTheProblemUnchanged()
    {
        Put("docker-compose.yml", "services:\n  api:\n    build: ./api\n    image: shop/api\n");
        var projects = new MemoryProjects(); await projects.SaveAsync(Project(_repo));
        var configs = new MemoryConfigs(); await configs.SaveAsync("p1", Cfg());

        var json = Json(await JitDeployRoutes.HandleGetConfigAsync("p1", projects, configs));
        StringAssert.Contains(json, "This project has no services to deploy.");
        StringAssert.Contains(json, "\"serviceHelp\":{");
        StringAssert.Contains(json, "\"explanation\":");
        StringAssert.Contains(json, "\"name\":\"api\"");
    }

    [TestMethod]
    public async Task ThereIsNoHelp_WhenThereIsNothingToExplain()
    {
        var projects = new MemoryProjects(); await projects.SaveAsync(Project(_repo, new ServiceConfig { Name = "api", DockerImageName = "shop/api" }));
        var configs = new MemoryConfigs(); await configs.SaveAsync("p1", Cfg());
        StringAssert.Contains(Json(await JitDeployRoutes.HandleGetConfigAsync("p1", projects, configs)), "\"serviceHelp\":null");
    }

    [TestMethod]
    public void ADeployThatCannotStart_IsToldWhatToDo_NotJustTheProblem()
    {
        var msg = JitServiceHelp.StartMessage([JitServiceHelp.NoServices]);
        StringAssert.StartsWith(msg, JitServiceHelp.NoServices);
        StringAssert.Contains(msg, "compose file");
        StringAssert.Contains(msg, "Build on the server");
        Assert.AreEqual("Enter the git address.", JitServiceHelp.StartMessage(["Enter the git address."]), "other problems are passed on exactly as they were");
    }
}
