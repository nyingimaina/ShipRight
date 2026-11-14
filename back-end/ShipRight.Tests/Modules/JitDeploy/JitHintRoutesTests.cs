using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.JitDeploy;
using ShipRight.Modules.Projects;
using ShipRight.Shared.ProcessRunner;
using ShipRight.Shared.SshRunner;

namespace ShipRight.Tests.Modules.JitDeploy;

[TestClass]
public class JitHintRoutesTests
{
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

    private sealed class FakeSsh : ISshRunner
    {
        public string[] Output { get; set; } = [];
        public int Exit { get; set; }
        public Exception? Throw { get; set; }
        public List<string> Commands { get; } = [];
        public async Task<int> RunAsync(string host, string username, string keyPath, string command,
            Func<string, Task>? onOutput = null, Func<string, Task>? onStderr = null, CancellationToken ct = default)
        {
            Commands.Add(command);
            if (Throw is not null) throw Throw;
            if (onOutput is not null) foreach (var l in Output) await onOutput(l);
            return Exit;
        }
    }

    private sealed class NoGit : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string executable, string[] args, string? workingDir, Func<string, Task>? onOutput = null,
            Func<string, Task>? onError = null, CancellationToken ct = default, IReadOnlyDictionary<string, string>? envOverride = null,
            TimeSpan? timeout = null, string? stdin = null) => Task.FromResult(new ProcessResult(1, "", "", TimeSpan.Zero));
    }

    private static ProjectConfig WithServer(bool server = true) => new()
    {
        Id = "p1", Name = "Shop",
        GitRepos = [new GitConfig { RepoPath = "/r", DeployBranch = "main" }],
        Server = server
            ? new ServerConfig { Host = "h", Username = "u", SshKeyPath = "/k", RemoteWorkingDir = "/home/u/shop", EnvFile = "/home/u/shop/.env" }
            : new ServerConfig(),
    };

    private static async Task<MemoryProjects> Store(ProjectConfig p) { var s = new MemoryProjects(); await s.SaveAsync(p); return s; }
    private static int Status(IResult r) => ((IStatusCodeHttpResult)r).StatusCode ?? 200;
    private static string Json(IResult r) => JsonSerializer.Serialize(((IValueHttpResult)r).Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    // ── hints ────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Hints_UnknownProject_Is404() =>
        Assert.AreEqual(404, Status(await JitHintRoutes.HandleHintsAsync("x", new MemoryProjects(), new NoGit())));

    [TestMethod]
    public async Task Hints_ReturnsWhatIsKnown_EvenWhenGitIsUnavailable()
    {
        var r = await JitHintRoutes.HandleHintsAsync("p1", await Store(WithServer()), new NoGit());
        Assert.AreEqual(200, Status(r));
        var json = Json(r);
        StringAssert.Contains(json, "\"envFile\":\"/home/u/shop/.env\"");
        StringAssert.Contains(json, "\"branch\":\"main\"");
        StringAssert.Contains(json, "\"composeProject\":\"shop\"");
    }

    // ── stacks ───────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Stacks_UnknownProject_Is404() =>
        Assert.AreEqual(404, Status(await JitHintRoutes.HandleStacksAsync("x", new MemoryProjects(), new MemoryConfigs(), new FakeSsh())));

    [TestMethod]
    public async Task Stacks_WithNoServerYet_IsAPlain400_AndTouchesNothing()
    {
        var ssh = new FakeSsh();
        var r = await JitHintRoutes.HandleStacksAsync("p1", await Store(WithServer(false)), new MemoryConfigs(), ssh);
        Assert.AreEqual(400, Status(r));
        StringAssert.Contains(Json(r), "server");
        Assert.AreEqual(0, ssh.Commands.Count);
    }

    [TestMethod]
    public async Task Stacks_ListsWhatRuns_AndWarnsAboutAStackUnderAnotherName()
    {
        var ssh = new FakeSsh { Output = ["[{\"Name\":\"shop-old\",\"Status\":\"running(2)\",\"ConfigFiles\":\"/home/u/shop/docker-compose.yml\"}]"] };
        var r = await JitHintRoutes.HandleStacksAsync("p1", await Store(WithServer()), new MemoryConfigs(), ssh);

        Assert.AreEqual(200, Status(r));
        var json = Json(r);
        StringAssert.Contains(json, "shop-old");
        StringAssert.Contains(json, "second copy");
        StringAssert.Contains(json, "\"match\":\"different\"");
        StringAssert.Contains(json, "\"suggestedName\":\"shop-old\"");
        Assert.AreEqual(JitStacks.ListCommand(), ssh.Commands.Single());
    }

    [TestMethod]
    public async Task Stacks_UsesTheChosenStackNameFromTheSavedSettings()
    {
        var configs = new MemoryConfigs();
        await configs.SaveAsync("p1", new JitDeployConfig { ComposeProject = "shop-old" });
        var ssh = new FakeSsh { Output = ["[{\"Name\":\"shop-old\",\"Status\":\"running(2)\",\"ConfigFiles\":\"/home/u/shop/repo/docker-compose.yml\"}]"] };
        var r = await JitHintRoutes.HandleStacksAsync("p1", await Store(WithServer()), configs, ssh);
        StringAssert.Contains(Json(r), "\"match\":\"same\"");
    }

    [TestMethod]
    public async Task Stacks_WhenDockerOrTheServerCannotAnswer_IsAPlainMessage_NotAnError()
    {
        foreach (var ssh in new[] { new FakeSsh { Exit = 127 }, new FakeSsh { Throw = new IOException("connection lost") } })
        {
            var r = await JitHintRoutes.HandleStacksAsync("p1", await Store(WithServer()), new MemoryConfigs(), ssh);
            Assert.AreEqual(200, Status(r));
            StringAssert.Contains(Json(r), "Could not list");
        }
    }

    // ── branches ─────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Branches_UnknownProject_Is404() =>
        Assert.AreEqual(404, Status(await JitHintRoutes.HandleBranchesAsync("x", new BranchesRequest("git@h:o/r.git"), new MemoryProjects(), new FakeSsh())));

    [TestMethod]
    public async Task Branches_RefusesAnUnsafeAddress_AndAnAddressWithASecret_Plainly()
    {
        var ssh = new FakeSsh();
        var store = await Store(WithServer());
        var bad = await JitHintRoutes.HandleBranchesAsync("p1", new BranchesRequest("--upload-pack=x"), store, ssh);
        Assert.AreEqual(400, Status(bad));
        var secret = await JitHintRoutes.HandleBranchesAsync("p1", new BranchesRequest("https://me:pw@github.com/o/r.git"), store, ssh);
        Assert.AreEqual(400, Status(secret));
        StringAssert.Contains(Json(secret), "password or token");
        Assert.IsFalse(Json(secret).Contains("pw@"));
        Assert.AreEqual(0, ssh.Commands.Count);
    }

    [TestMethod]
    public async Task Branches_AreReadOnTheServer_NotOnThisComputer()
    {
        var ssh = new FakeSsh { Output = ["ref: refs/heads/main\tHEAD", "2222222222222222222222222222222222222222\trefs/heads/main", "3333333333333333333333333333333333333333\trefs/heads/dev"] };
        var r = await JitHintRoutes.HandleBranchesAsync("p1", new BranchesRequest("git@github.com:o/r.git"), await Store(WithServer()), ssh);

        Assert.AreEqual(200, Status(r));
        var json = Json(r);
        StringAssert.Contains(json, "\"defaultBranch\":\"main\"");
        StringAssert.Contains(json, "dev");
        Assert.AreEqual(JitBranches.Command("git@github.com:o/r.git"), ssh.Commands.Single());
    }

    [TestMethod]
    public async Task Branches_WhenTheServerCannotReachTheRepo_SaysSoWithoutTheAddress()
    {
        var ssh = new FakeSsh { Exit = 1, Output = ["[branches] Cannot read the branches: the server cannot reach that repository yet."] };
        var r = await JitHintRoutes.HandleBranchesAsync("p1", new BranchesRequest("git@github.com:secret-org/r.git"), await Store(WithServer()), ssh);

        Assert.AreEqual(200, Status(r));
        var json = Json(r);
        StringAssert.Contains(json, "cannot reach that repository");
        Assert.IsFalse(json.Contains("secret-org"));
    }

    [TestMethod]
    public async Task Branches_WithNoServerYet_IsAPlain400()
    {
        var r = await JitHintRoutes.HandleBranchesAsync("p1", new BranchesRequest("git@h:o/r.git"), await Store(WithServer(false)), new FakeSsh());
        Assert.AreEqual(400, Status(r));
    }

    private sealed class MemoryConfigs : IJitConfigStore
    {
        private readonly Dictionary<string, JitDeployConfig> _all = [];
        public Task<JitDeployConfig?> GetAsync(string projectId) => Task.FromResult(_all.GetValueOrDefault(projectId));
        public Task SaveAsync(string projectId, JitDeployConfig config) { _all[projectId] = config; return Task.CompletedTask; }
        public Task DeleteAsync(string projectId) { _all.Remove(projectId); return Task.CompletedTask; }
    }
}
