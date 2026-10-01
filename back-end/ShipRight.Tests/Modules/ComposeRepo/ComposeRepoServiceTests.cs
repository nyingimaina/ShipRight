using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.ComposeRepo;
using ShipRight.Modules.Projects;
using ShipRight.Shared.ProcessRunner;

namespace ShipRight.Tests.Modules.ComposeRepo;

[TestClass]
public class ComposeRepoServiceTests : IDisposable
{
    private const string ComposeOrigin = "https://dev.azure.com/org/DefaultCollection/lattice-docker/_git/lattice-docker";
    private const string AppRepoOrigin   = "https://dev.azure.com/org/DefaultCollection/dailies-backend/_git/dailies-backend";

    private readonly string _tmpDir;
    private readonly string _composeDir;
    private readonly FakeProjectStore _projectStore = new();
    private readonly FakeProcessRunner _runner = new();

    public ComposeRepoServiceTests()
    {
        _tmpDir    = Path.Combine(Path.GetTempPath(), $"sr_compose_{Guid.NewGuid():N}");
        _composeDir = Path.Combine(_tmpDir, "compose");
        Directory.CreateDirectory(_composeDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tmpDir)) Directory.Delete(_tmpDir, recursive: true);
    }

    private ComposeRepoService NewService() => new(_runner, _projectStore);

    private static ProjectConfig Project(string composeUrl = "") => new()
    {
        Id = "p1",
        Name = "Lattice Backend",
        GitRepos = [new GitConfig { RepoPath = @"D:\code\app", DeployBranch = "master" }],
        Wsl = new WslConfig { WorkingDir = "/home/ubuntu/compose", ComposeRepoUrl = composeUrl },
        Server = new ServerConfig { DeployMode = DeployMode.GitScript },
    };

    /// <summary>Fake WSL/git results: dir exists, rev-parse ok, origin, optional fsck corruption.</summary>
    private void SetupGit(Func<string[], bool>? fsckCorrupt = null, string origin = ComposeOrigin)
    {
        _runner.SetResultFactory((exe, args) =>
        {
            if (exe == "wsl" && args.Contains("wslpath"))
                return Ok(_composeDir);
            if (args.Contains("--is-inside-work-tree"))
                return Directory.Exists(_composeDir) ? Ok("true") : Fail("fatal: not a git repository");
            if (args.Contains("get-url"))
                return Ok(origin);
            if (args.Contains("fsck"))
            {
                var corrupt = fsckCorrupt?.Invoke(args) ?? false;
                return corrupt
                    ? Fail("error: object file .git/objects/7f/030bce is empty\nfatal: loose object 7f030bce is corrupt")
                    : Ok("");
            }
            return Ok("");
        });
    }

    private static ProcessResult Ok(string stdout)  => new(0, stdout, "", TimeSpan.Zero);
    private static ProcessResult Fail(string stderr) => new(1, "", stderr, TimeSpan.Zero);

    // ── Status ───────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task GetStatusAsync_DirectoryMissing_ReportsNotExists()
    {
        Directory.Delete(_composeDir);
        SetupGit();

        var status = await NewService().GetStatusAsync(Project());

        Assert.IsFalse(status.Exists);
        Assert.IsFalse(status.IsGitRepo);
        Assert.IsTrue(status.Health.IsHealthy, "A missing dir is not 'corrupt' — it is simply absent.");
    }

    [TestMethod]
    public async Task GetStatusAsync_HealthyRepo_ReportsHealthyAndPersistsCloneUrl()
    {
        SetupGit();
        var project = Project();
        _projectStore.Add(project);

        var status = await NewService().GetStatusAsync(project);

        Assert.IsTrue(status.Exists);
        Assert.IsTrue(status.IsGitRepo);
        Assert.IsTrue(status.Health.IsHealthy, status.Health.Summary);
        Assert.AreEqual(ComposeOrigin, status.CloneUrl);
        Assert.AreEqual(ComposeOrigin, _projectStore.Get(project.Id)!.Wsl.ComposeRepoUrl,
            "The compose repo's own origin must be persisted so it survives a later delete.");
    }

    [TestMethod]
    public async Task GetStatusAsync_EmptyLooseObjects_ReportsUnhealthy()
    {
        SetupGit(fsckCorrupt: _ => true);
        var project = Project();
        _projectStore.Add(project);

        var status = await NewService().GetStatusAsync(project);

        Assert.IsFalse(status.Health.IsHealthy, "fsck reported corrupt loose objects — the repo is not usable.");
        StringAssert.Contains(status.Health.Summary, "empty");
    }

    [TestMethod]
    public async Task GetStatusAsync_FsckExitsZeroButPrintsCorruption_StillReportsUnhealthy()
    {
        // Defensive: some git builds print errors and still exit 0.
        _runner.SetResultFactory((exe, args) =>
        {
            if (exe == "wsl" && args.Contains("wslpath")) return Ok(_composeDir);
            if (args.Contains("--is-inside-work-tree")) return Ok("true");
            if (args.Contains("fsck")) return Ok("error: object file .git/objects/aa/bb is empty");
            return Ok("");
        });
        var project = Project();
        _projectStore.Add(project);

        var status = await NewService().GetStatusAsync(project);

        Assert.IsFalse(status.Health.IsHealthy);
    }

    // ── Clone URL resolution (the GitRepos[0] mis-derivation bug) ────────────

    [TestMethod]
    public async Task ResolveCloneUrlAsync_PrefersPersistedComposeUrl_OverAppRepoOrigin()
    {
        SetupGit(origin: "https://dev.azure.com/org/DefaultCollection/some-other-compose/_git/x");
        var project = Project(ComposeOrigin);
        _projectStore.Add(project);

        var url = await NewService().ResolveCloneUrlAsync(project);

        Assert.AreEqual(ComposeOrigin, url,
            "The configured compose-repo URL must win over the live repo origin and over GitRepos[0].");
    }

    [TestMethod]
    public async Task ResolveCloneUrlAsync_PrefersLiveRepoOrigin_WhenNoUrlConfigured()
    {
        SetupGit(origin: "https://dev.azure.com/org/DefaultCollection/live-compose/_git/live-compose");
        var project = Project();
        _projectStore.Add(project);

        var url = await NewService().ResolveCloneUrlAsync(project);

        Assert.AreEqual("https://dev.azure.com/org/DefaultCollection/live-compose/_git/live-compose", url);
    }

    [TestMethod]
    public async Task ResolveCloneUrlAsync_FallsBackToFirstAppRepoOrigin_ForBackCompat()
    {
        Directory.Delete(_composeDir);
        _runner.SetResultFactory((exe, args) =>
        {
            if (exe == "wsl" && args.Contains("wslpath")) return Ok(_composeDir);
            if (args.Contains("--is-inside-work-tree")) return Fail("fatal: not a git repository");
            if (args.Contains("get-url")) return Ok(AppRepoOrigin);
            return Ok("");
        });
        var project = Project();
        _projectStore.Add(project);

        var url = await NewService().ResolveCloneUrlAsync(project);

        Assert.AreEqual(AppRepoOrigin, url,
            "Existing single-repo projects rely on the legacy GitRepos[0] derivation; it must keep working.");
    }

    // ── Archive / clone / repair ─────────────────────────────────────────────

    [TestMethod]
    public async Task ArchiveAsync_MovesDirectoryAside_AndReturnsRecoveryPath()
    {
        SetupGit();
        var project = Project();
        _projectStore.Add(project);
        var target = new DirectoryInfo(_composeDir);
        File.WriteAllText(Path.Combine(_composeDir, "docker-compose.yml"), "services: {}");

        var archivePath = await NewService().ArchiveAsync(project);

        Assert.IsFalse(Directory.Exists(_composeDir), "The compose working dir must be gone after archiving.");
        Assert.IsTrue(Directory.Exists(archivePath), "The archived copy must remain for recovery.");
        Assert.IsTrue(File.Exists(Path.Combine(archivePath, "docker-compose.yml")));
        StringAssert.Contains(Path.GetFileName(archivePath), "compose");
    }

    [TestMethod]
    public async Task CloneAsync_RunsGitClone_WithResolvedUrl()
    {
        SetupGit();
        var project = Project(ComposeOrigin);
        _projectStore.Add(project);

        await NewService().CloneAsync(project, ComposeOrigin);

        var clone = _runner.Calls.First(c => c.Executable == "git" && c.Args.Contains("clone"));
        CollectionAssert.AreEqual(new[] { "clone", ComposeOrigin, project.Wsl.WorkingDir }, clone.Args);
    }

    [TestMethod]
    public async Task CloneAsync_WhenGitFails_ThrowsWithGitStderr()
    {
        _runner.SetResultFactory((exe, args) =>
            args.Contains("clone") ? Fail("fatal: could not read Username for 'https://dev.azure.com'")
                                   : Ok(""));
        var project = Project(ComposeOrigin);
        _projectStore.Add(project);

        var ex = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => NewService().CloneAsync(project, ComposeOrigin));
        StringAssert.Contains(ex.Message, "could not read Username");
    }

    [TestMethod]
    public async Task RepairAsync_ArchivesThenClones_SoRepairNeverRunsInsideTheCorruptDir()
    {
        SetupGit(fsckCorrupt: _ => true);
        var project = Project(ComposeOrigin);
        _projectStore.Add(project);

        var archivePath = await NewService().RepairAsync(project, ComposeOrigin);

        Assert.IsTrue(Directory.Exists(archivePath), "Corrupt tree is kept for recovery, not destroyed.");
        Assert.IsFalse(Directory.Exists(_composeDir));
        Assert.IsTrue(_runner.Calls.Any(c => c.Executable == "git" && c.Args.Contains("clone")),
            "Repair must leave a freshly cloned repo behind.");
    }

    [TestMethod]
    public async Task PersistCloneUrlAsync_StoresUrlOnProject()
    {
        var project = Project();
        _projectStore.Add(project);

        await NewService().PersistCloneUrlAsync(project, ComposeOrigin);

        Assert.AreEqual(ComposeOrigin, _projectStore.Get(project.Id)!.Wsl.ComposeRepoUrl);
    }

    private sealed class FakeProjectStore : IProjectStore
    {
        private readonly Dictionary<string, ProjectConfig> _projects = new();
        public ProjectConfig? Get(string id) => _projects.TryGetValue(id, out var p) ? p : null;
        public void Add(ProjectConfig p) => _projects[p.Id] = p;
        public int Count => _projects.Count;
        public Task<List<ProjectConfig>> GetAllAsync() => Task.FromResult(_projects.Values.ToList());
        public Task<ProjectConfig?> GetByIdAsync(string id) => Task.FromResult(Get(id));
        public Task<ProjectConfig?> GetByNameAsync(string name) =>
            Task.FromResult(_projects.Values.FirstOrDefault(p => p.Name == name));
        public Task SaveAsync(ProjectConfig project) { _projects[project.Id] = project; return Task.CompletedTask; }
        public Task DeleteAsync(string id) { _projects.Remove(id); return Task.CompletedTask; }
    }

    private sealed class FakeProcessRunner : IProcessRunner
    {
        private Func<string, string[], ProcessResult> _resultFactory = (_, _) => new(0, "", "", TimeSpan.Zero);
        public List<(string Executable, string[] Args, string? Stdin)> Calls { get; } = [];
        public void SetResultFactory(Func<string, string[], ProcessResult> factory) => _resultFactory = factory;

        public Task<ProcessResult> RunAsync(
            string executable, string[] args, string? workingDir,
            Func<string, Task>? onOutput = null, Func<string, Task>? onError = null,
            CancellationToken ct = default, IReadOnlyDictionary<string, string>? envOverride = null,
            TimeSpan? timeout = null, string? stdin = null)
        {
            Calls.Add((executable, args, stdin));
            return Task.FromResult(_resultFactory(executable, args));
        }
    }
}
