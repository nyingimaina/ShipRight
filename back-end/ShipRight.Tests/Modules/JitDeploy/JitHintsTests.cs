using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.JitDeploy;
using ShipRight.Modules.Projects;
using ShipRight.Shared.ProcessRunner;

namespace ShipRight.Tests.Modules.JitDeploy;

/// <summary>What ShipRight already knows about a project that the build-on-server card should offer instead of asking.</summary>
[TestClass]
public class JitHintsTests
{
    private sealed class FakeRunner : IProcessRunner
    {
        public Dictionary<string, string?> Origins { get; } = [];     // repo path -> origin (null = no origin)
        public List<(string Exe, string[] Args)> Calls { get; } = [];

        public Task<ProcessResult> RunAsync(string executable, string[] args, string? workingDir,
            Func<string, Task>? onOutput = null, Func<string, Task>? onError = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? envOverride = null, TimeSpan? timeout = null, string? stdin = null)
        {
            Calls.Add((executable, args));
            var path = args.Length > 1 ? args[1] : "";
            return Task.FromResult(Origins.TryGetValue(path, out var o) && o is not null
                ? new ProcessResult(0, o + "\n", "", TimeSpan.Zero)
                : new ProcessResult(2, "", "error: No such remote 'origin'", TimeSpan.Zero));
        }
    }

    private static ProjectConfig Project(Action<List<GitConfig>>? repos = null, string compose = "", string serverCompose = "", string env = "", string? watch = null)
    {
        var list = new List<GitConfig> { new() { RepoPath = "/repos/shop", DeployBranch = "main" } };
        repos?.Invoke(list);
        return new ProjectConfig
        {
            Name = "Shop Front",
            GitRepos = list,
            WatchBranch = watch,
            Wsl = new WslConfig { ComposeRepoUrl = compose },
            Server = new ServerConfig { ComposeRepoUrl = serverCompose, EnvFile = env },
        };
    }

    private static async Task<JitHints> Hints(ProjectConfig p, FakeRunner r) => await new JitHintsService(r).BuildAsync(p);

    [TestMethod]
    public async Task AProjectRepo_IsOfferedWithItsOrigin_BothWaysOfWritingIt_AndItsDeployBranch()
    {
        var r = new FakeRunner { Origins = { ["/repos/shop"] = "git@github.com:o/shop.git" } };
        var h = await Hints(Project(), r);

        var repo = h.Repos.Single();
        Assert.AreEqual("git@github.com:o/shop.git", repo.Ssh);
        Assert.AreEqual("https://github.com/o/shop.git", repo.Https);
        Assert.AreEqual("main", repo.Branch);
        Assert.AreEqual("project-repo", repo.Source);
        StringAssert.Contains(repo.Label, "shop");
        Assert.AreEqual("main", h.Branch);
    }

    [TestMethod]
    public async Task OnlyTheOriginIsReadFromTheRepo_AndNothingElseIsRun()
    {
        var r = new FakeRunner { Origins = { ["/repos/shop"] = "git@github.com:o/shop.git" } };
        await Hints(Project(), r);

        var call = r.Calls.Single();
        Assert.AreEqual("git", call.Exe);
        CollectionAssert.AreEqual(new[] { "-C", "/repos/shop", "remote", "get-url", "origin" }, call.Args);
    }

    [TestMethod]
    public async Task ThePasswordInAnOriginIsNeverReturned_AndTheUserIsTold()
    {
        var r = new FakeRunner { Origins = { ["/repos/shop"] = "https://me:ghp_secret@github.com/o/shop.git" } };
        var h = await Hints(Project(), r);

        var json = System.Text.Json.JsonSerializer.Serialize(h);
        Assert.IsFalse(json.Contains("ghp_secret"));
        Assert.IsTrue(h.Repos.Single().HadCredentials);
        Assert.IsTrue(h.Notes.Any(n => n.Contains("password or token")));
    }

    [TestMethod]
    public async Task TheComposeRepo_IsOfferedToo_AndAnAddressThatIsTheSameRepoAppearsOnce()
    {
        var r = new FakeRunner { Origins = { ["/repos/shop"] = "git@github.com:o/shop.git" } };
        var h = await Hints(Project(compose: "https://github.com/o/shop", serverCompose: "https://github.com/o/infra.git"), r);

        Assert.AreEqual(2, h.Repos.Count);
        CollectionAssert.AreEqual(new[] { "project-repo", "compose-repo" }, h.Repos.Select(x => x.Source).ToArray());
        StringAssert.Contains(h.Repos[1].Label, "ompose");
    }

    [TestMethod]
    public async Task WithNoLocalRepo_TheComposeRepoAloneIsOffered()
    {
        var h = await Hints(Project(repos: l => l.Clear(), compose: "git@github.com:o/infra.git"), new FakeRunner());
        Assert.AreEqual("compose-repo", h.Repos.Single().Source);
    }

    [TestMethod]
    public async Task ARepoWithNoOrigin_IsLeftOut_WithAPlainNote()
    {
        var h = await Hints(Project(), new FakeRunner());
        Assert.AreEqual(0, h.Repos.Count);
        Assert.IsTrue(h.Notes.Any(n => n.Contains("no git address")));
    }

    [TestMethod]
    public async Task ARepoWithAnEmptyPath_IsIgnoredWithoutRunningAnything()
    {
        var r = new FakeRunner();
        await Hints(Project(repos: l => { l.Clear(); l.Add(new GitConfig { RepoPath = " " }); }), r);
        Assert.AreEqual(0, r.Calls.Count);
    }

    [TestMethod]
    public async Task SeveralRepos_ProduceAnExplanatoryNote()
    {
        var r = new FakeRunner { Origins = { ["/repos/shop"] = "git@github.com:o/shop.git", ["/repos/web"] = "git@github.com:o/web.git" } };
        var h = await Hints(Project(repos: l => l.Add(new GitConfig { RepoPath = "/repos/web", DeployBranch = "master" })), r);

        Assert.AreEqual(2, h.Repos.Count);
        Assert.IsTrue(h.Notes.Any(n => n.Contains("one repository")));
    }

    [TestMethod]
    public async Task TheEnvFileAndTheDefaultStackNameComeFromTheProject()
    {
        var h = await Hints(Project(env: " /home/u/shop/.env "), new FakeRunner());
        Assert.AreEqual("/home/u/shop/.env", h.EnvFile);
        Assert.AreEqual("shop-front", h.ComposeProject);
    }

    [TestMethod]
    public async Task NoEnvFileKnown_IsNull() =>
        Assert.IsNull((await Hints(Project(), new FakeRunner())).EnvFile);

    [TestMethod]
    public async Task TheBranchComesFromTheDeployBranch_ThenTheWatchBranch_ElseNothing()
    {
        Assert.AreEqual("main", (await Hints(Project(), new FakeRunner())).Branch);
        Assert.AreEqual("release", (await Hints(Project(repos: l => l[0] = new GitConfig { RepoPath = "/repos/shop", DeployBranch = "" }, watch: "release"), new FakeRunner())).Branch);
        Assert.IsNull((await Hints(Project(repos: l => l[0] = new GitConfig { RepoPath = "/repos/shop", DeployBranch = "" }), new FakeRunner())).Branch);
    }

    [TestMethod]
    public async Task AFailingRunner_NeverBreaksTheHints()
    {
        var h = await new JitHintsService(new ThrowingRunner()).BuildAsync(Project(env: "/e"));
        Assert.AreEqual(0, h.Repos.Count);
        Assert.AreEqual("/e", h.EnvFile);
    }

    private sealed class ThrowingRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string executable, string[] args, string? workingDir, Func<string, Task>? onOutput = null,
            Func<string, Task>? onError = null, CancellationToken ct = default, IReadOnlyDictionary<string, string>? envOverride = null,
            TimeSpan? timeout = null, string? stdin = null) => throw new InvalidOperationException("git is not installed");
    }
}
