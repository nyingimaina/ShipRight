using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.JitDeploy;
using ShipRight.Modules.Projects;
using ShipRight.Shared.ProcessRunner;
using ShipRight.Shared.SshRunner;
using ShipRight.Tests.Modules.Setup;

namespace ShipRight.Tests.Modules.JitDeploy;

/// <summary>
/// A dry run must be useful against production: it can read, but it must never send a command that clones, fetches,
/// resets, writes, builds, stops, starts or removes anything. These tests pin both the plan and the absence of writes.
/// </summary>
[TestClass]
public class JitDryRunTests
{
    private const string Repo = @"D:\work\shop";
    private const string Origin = "git@github.com:o/r.git";
    private const string Sha = "3f2a9c1d0e8b7a6f5e4d3c2b1a0f9e8d7c6b5a4f";

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
        public Task<JitDeployConfig?> GetAsync(string projectId) => Task.FromResult(Items.GetValueOrDefault(projectId));
        public Task SaveAsync(string projectId, JitDeployConfig config) { Items[projectId] = config; return Task.CompletedTask; }
        public Task DeleteAsync(string projectId) { Items.Remove(projectId); return Task.CompletedTask; }
    }

    private sealed class Ssh : ISshRunner
    {
        public List<string> Commands { get; } = [];
        public Func<string, (int Exit, string[] Lines)> Handler { get; set; } = _ => (0, []);

        public async Task<int> RunAsync(string host, string username, string keyPath, string command,
            Func<string, Task>? onOutput = null, Func<string, Task>? onStderr = null, CancellationToken ct = default)
        {
            Commands.Add(command);
            var (exit, lines) = Handler(command);
            if (onOutput is not null) foreach (var l in lines) await onOutput(l);
            return exit;
        }
    }

    private static ProjectConfig Project() => new()
    {
        Id = "p1", Name = "Shop",
        Services = [new ServiceConfig { Name = "api", DockerImageName = "shop/api", ComposeServiceName = "api" }],
        GitRepos = [new GitConfig { RepoPath = Repo, DeployBranch = "master" }],
        Server = new ServerConfig { Host = "h", Username = "u", SshKeyPath = "/k", RemoteWorkingDir = "/home/u/shop" },
    };

    private static JitDeployConfig Config() => new() { Enabled = true, RepoUrl = Origin, Branch = "master" };

    private static RecordingRunner LocalRunner(string status = "", string branch = "master", string counts = "0 0", bool lineEndingsOnly = false)
    {
        var r = new RecordingRunner();
        r.When((_, a) => a.Contains("remote") && a.Contains("get-url"), 0, Origin + "\n");
        r.When((_, a) => a.Contains("fetch"), 0, "");
        r.When((_, a) => a.Contains("--abbrev-ref"), 0, branch + "\n");
        r.When((_, a) => a.Any(x => x.StartsWith("--porcelain", StringComparison.Ordinal)), 0, status);
        r.When((_, a) => a.Contains("--ignore-cr-at-eol"), lineEndingsOnly ? 0 : 1, "");
        r.When((_, a) => a.Contains("--verify"), 0, Sha + "\n");
        r.When((_, a) => a.Contains("rev-list"), 0, counts + "\n");
        return r;
    }

    private static Ssh ReadOnlyServer() => new()
    {
        Handler = command =>
            command.Contains("df -Pm") ? (0, ["[preflight] Free disk space: 8000 MB"])
            : command.Contains(ReleaseStateStore.NoStateSentinel) ? (0, [ReleaseStateStore.NoStateSentinel])
            : (0, []),
    };

    private sealed class Rig : IDisposable
    {
        public TempTree Tree { get; } = new();
        public Projects Projects { get; } = new();
        public Configs Configs { get; } = new();
        public Ssh Ssh { get; }
        public RecordingRunner Local { get; }
        public JitDeployAttemptStore Store { get; }
        public JitDryRunService Service { get; }

        public Rig(RecordingRunner? local = null, Ssh? ssh = null)
        {
            Projects.Items["p1"] = Project();
            Configs.Items["p1"] = Config();
            Local = local ?? LocalRunner();
            Ssh = ssh ?? ReadOnlyServer();
            Store = new JitDeployAttemptStore(Tree.Root);
            Service = new JitDryRunService(Projects, Configs, new JitLocalBranchGuard(Local), Ssh, Local, Store);
        }

        public void Dispose() => Tree.Dispose();
    }

    [TestMethod]
    public async Task DryRun_ReturnsThePlanWithoutSendingAnyMutatingCommand()
    {
        using var rig = new Rig();

        var result = await rig.Service.PlanAsync("p1", null, "tester");

        Assert.IsTrue(result.Ok, result.Error);
        Assert.AreEqual(Sha, result.Commit);
        StringAssert.StartsWith(result.Tag!, "r1-");
        CollectionAssert.Contains(result.Services.ToList(), "api");
        Assert.IsTrue(result.Commands.Any(c => c.Command.Contains("docker compose") && c.Command.Contains("build")),
            "the plan shows the build that a real deploy would run");
        Assert.IsTrue(result.TargetChecked);
        Assert.IsNotNull(result.AttemptId);

        foreach (var command in rig.Ssh.Commands)
            Assert.IsTrue(IsReadOnly(command), $"a dry run must not send this to the target: {command}");
    }

    [TestMethod]
    public async Task DryRun_IsBlockedByDirtyLocalWork_AndStillLeavesADownloadableRecord()
    {
        using var rig = new Rig(local: LocalRunner(status: "M  src/Program.cs\n?? scratch.sql\n"));

        var result = await rig.Service.PlanAsync("p1", null, "tester");

        Assert.IsFalse(result.Ok);
        StringAssert.Contains(result.Error!, "Commit your local changes");
        Assert.AreEqual(0, rig.Ssh.Commands.Count, "the target is never contacted when the local guard blocks the deploy");

        var text = await rig.Store.ReadAsync("p1", result.AttemptId!);
        Assert.IsNotNull(text);
        StringAssert.Contains(text, "src/Program.cs");
        StringAssert.Contains(text, "Result: blocked");
    }

    [TestMethod]
    public async Task DryRun_ReportsGitAccessAndDiskAsWarnings_NotAsAFailure()
    {
        var ssh = ReadOnlyServer();
        ssh.Handler = command =>
            command.Contains("git ls-remote") ? (1, ["[preflight] Cannot reach the git repository."])
            : command.Contains("df -Pm") ? (0, ["[preflight] Free disk space: 100 MB"])
            : (0, []);
        using var rig = new Rig(ssh: ssh);

        var result = await rig.Service.PlanAsync("p1", null, "tester");

        Assert.IsTrue(result.Ok);
        Assert.IsTrue(result.Warnings.Any(w => w.Contains("Cannot reach the git repository")));
        Assert.IsTrue(result.Warnings.Any(w => w.Contains("100 MB") && w.Contains("3000 MB")));
    }

    [TestMethod]
    public async Task DryRun_WithAnExplicitCommit_DoesNotRunTheLocalGuard()
    {
        using var rig = new Rig();

        var result = await rig.Service.PlanAsync("p1", new JitDeployOptions(Commit: "ABCDEF1234"), "tester");

        Assert.IsTrue(result.Ok, result.Error);
        Assert.AreEqual("abcdef1234", result.Commit);
        Assert.AreEqual(0, rig.Local.Calls.Count);
        Assert.IsNull(result.GuardSummary);
    }

    [TestMethod]
    public async Task DryRun_UnknownProject_IsNotFound()
    {
        using var rig = new Rig();
        rig.Projects.Items.Clear();

        var result = await rig.Service.PlanAsync("nope", null, null);

        Assert.IsTrue(result.NotFound);
        Assert.IsFalse(result.Ok);
    }

    [TestMethod]
    public void ParseTipReadsOnlyTheExactBranch()
    {
        const string output = "1111111111111111111111111111111111111111\trefs/heads/main\n" +
                              "3f2a9c1d0e8b7a6f5e4d3c2b1a0f9e8d7c6b5a4f\trefs/heads/master\n";

        Assert.AreEqual("3f2a9c1d0e8b7a6f5e4d3c2b1a0f9e8d7c6b5a4f", JitDryRunService.ParseTip(output, "master"));
        Assert.IsNull(JitDryRunService.ParseTip(output, "missing"));
    }

    private static bool IsReadOnly(string command)
    {
        var mutations = new[]
        {
            "git clone", "git fetch", "git reset", "git clean", "git checkout", "git pull", "git push",
            "base64 -d", "docker image rm", "docker compose -p", "mkdir ", "rm ", "mv ",
            "docker compose up", "docker compose down", "docker compose stop", "docker compose build", "docker compose pull",
        };
        return !mutations.Any(m => command.Contains(m, StringComparison.Ordinal));
    }
}
