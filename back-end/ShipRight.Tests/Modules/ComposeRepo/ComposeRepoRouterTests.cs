using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Builds;
using ShipRight.Modules.ComposeRepo;
using ShipRight.Modules.Projects;
using ShipRight.Shared.Events;
using ShipRight.Shared.ProcessRunner;

namespace ShipRight.Tests.Modules.ComposeRepo;

[TestClass]
public class ComposeRepoRouterTests : IDisposable
{
    private const string ComposeOrigin = "https://dev.azure.com/org/DefaultCollection/lattice-docker/_git/lattice-docker";

    private readonly string _tmpDir;
    private readonly string _composeDir;
    private readonly InMemoryProjectStore _projectStore = new();
    private readonly InMemoryBuildStore _buildStore = new();
    private readonly BuildEventBus _bus = new();
    private readonly StubProcessRunner _runner = new();

    public ComposeRepoRouterTests()
    {
        _tmpDir     = Path.Combine(Path.GetTempPath(), $"sr_router_compose_{Guid.NewGuid():N}");
        _composeDir = Path.Combine(_tmpDir, "compose");
        Directory.CreateDirectory(_composeDir);
        _runner.WindowsDir = _composeDir;
        _runner.CloneUrl  = ComposeOrigin;
    }

    public void Dispose()
    {
        if (Directory.Exists(_tmpDir)) Directory.Delete(_tmpDir, recursive: true);
    }

    private ComposeRepoService NewService() => new(_runner, _projectStore);

    private static ProjectConfig Project(string id = "p1", string composeUrl = "") => new()
    {
        Id = id,
        Name = "Lattice Backend",
        GitRepos = [new GitConfig { RepoPath = @"D:\code\app", DeployBranch = "master" }],
        Wsl = new WslConfig { WorkingDir = "/home/ubuntu/compose", ComposeRepoUrl = composeUrl },
        Server = new ServerConfig { DeployMode = DeployMode.GitScript },
    };

    // ── Status ───────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task HandleGetStatusAsync_UnknownProject_ReturnsNotFound()
    {
        var result = await ComposeRepoRouter.HandleGetStatusAsync("nope", _projectStore, NewService());
        Assert.AreEqual(StatusCodes.Status404NotFound, StatusOf(result));
    }

    [TestMethod]
    public async Task HandleGetStatusAsync_KnownProject_ReturnsWorkingDirAndHealth()
    {
        _projectStore.Add(Project());

        var result = await ComposeRepoRouter.HandleGetStatusAsync("p1", _projectStore, NewService());

        Assert.AreEqual(StatusCodes.Status200OK, StatusOf(result));
        var body = BodyOf(result);
        Assert.AreEqual("/home/ubuntu/compose", body.GetProperty("wslPath").GetString());
        Assert.IsTrue(body.GetProperty("exists").GetBoolean());
        Assert.AreEqual(ComposeOrigin, body.GetProperty("cloneUrl").GetString());
        Assert.IsTrue(body.GetProperty("health").GetProperty("isHealthy").GetBoolean());
    }

    // ── Clone URL persistence (the wrong-origin fix) ─────────────────────────

    [TestMethod]
    public async Task HandleSetCloneUrlAsync_PersistsUrlToProject()
    {
        var project = Project();
        _projectStore.Add(project);
        // The live repo is gone, so GitRepos[0] would be the only other candidate — the wrong one.
        Directory.Delete(_composeDir);
        _runner.IsGitRepo = false;

        var result = await ComposeRepoRouter.HandleSetCloneUrlAsync(
            "p1", new ComposeRepoRouter.SetCloneUrlRequest(ComposeOrigin), _projectStore);

        Assert.AreEqual(StatusCodes.Status200OK, StatusOf(result));
        Assert.AreEqual(ComposeOrigin, _projectStore.Get("p1")!.Wsl.ComposeRepoUrl);
    }

    [TestMethod]
    public async Task HandleSetCloneUrlAsync_BlankUrl_ReturnsBadRequest()
    {
        _projectStore.Add(Project());

        var result = await ComposeRepoRouter.HandleSetCloneUrlAsync(
            "p1", new ComposeRepoRouter.SetCloneUrlRequest("   "), _projectStore);

        Assert.AreEqual(StatusCodes.Status400BadRequest, StatusOf(result));
    }

    // ── Clone ────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task HandleStartCloneAsync_NoUrlResolvable_ReturnsBadRequestWithGuidance()
    {
        Directory.Delete(_composeDir);
        _runner.IsGitRepo = false;
        // No ComposeRepoUrl, no live repo origin, and no app repo to fall back on.
        _projectStore.Add(Project() with { GitRepos = [] });

        var result = await ComposeRepoRouter.HandleStartCloneAsync(
            "p1", _projectStore, _buildStore, NewService(), _bus);

        Assert.AreEqual(StatusCodes.Status400BadRequest, StatusOf(result));
    }

    [TestMethod]
    public async Task HandleStartCloneAsync_WithUrl_StartsOpAndClones()
    {
        Directory.Delete(_composeDir);
        _runner.IsGitRepo = false;
        _projectStore.Add(Project(composeUrl: ComposeOrigin));

        var result = await ComposeRepoRouter.HandleStartCloneAsync(
            "p1", _projectStore, _buildStore, NewService(), _bus);

        Assert.AreEqual(StatusCodes.Status202Accepted, StatusOf(result));
        var opId = BodyOf(result).GetProperty("opId").GetString()!;
        await WaitForOpAsync(opId);
        Assert.IsTrue(_runner.Calls.Any(c => c.Executable == "git" && c.Args.Contains("clone")),
            "The clone op must actually run git clone.");
    }

    [TestMethod]
    public async Task HandleStartCloneAsync_WhileBuildRunning_ReturnsConflict()
    {
        _projectStore.Add(Project(composeUrl: ComposeOrigin));
        _buildStore.Add(new BuildRecord { Id = "b1", ProjectId = "p1", Status = BuildStatus.Running });

        var result = await ComposeRepoRouter.HandleStartCloneAsync(
            "p1", _projectStore, _buildStore, NewService(), _bus);

        Assert.AreEqual(StatusCodes.Status409Conflict, StatusOf(result));
    }

    // ── Delete ───────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task HandleStartDeleteAsync_WhileBuildPaused_ReturnsConflict()
    {
        _projectStore.Add(Project());
        _buildStore.Add(new BuildRecord { Id = "b1", ProjectId = "p1", Status = BuildStatus.Paused });

        var result = await ComposeRepoRouter.HandleStartDeleteAsync(
            "p1", _projectStore, _buildStore, NewService(), _bus);

        Assert.AreEqual(StatusCodes.Status409Conflict, StatusOf(result));
        Assert.IsTrue(Directory.Exists(_composeDir), "A refused delete must not touch the directory.");
    }

    [TestMethod]
    public async Task HandleStartDeleteAsync_Idle_ArchivesRepoAndKeepsCloneUrl()
    {
        _projectStore.Add(Project());

        var result = await ComposeRepoRouter.HandleStartDeleteAsync(
            "p1", _projectStore, _buildStore, NewService(), _bus);

        Assert.AreEqual(StatusCodes.Status202Accepted, StatusOf(result));
        var opId = BodyOf(result).GetProperty("opId").GetString()!;
        var logs = await WaitForOpAsync(opId);

        Assert.IsFalse(Directory.Exists(_composeDir), "The compose working dir must be removed.");
        var archive = Directory.GetDirectories(_tmpDir, "compose.corrupt-*");
        Assert.AreEqual(1, archive.Length, "The removed tree must be recoverable, not destroyed.");
        Assert.AreEqual(ComposeOrigin, _projectStore.Get("p1")!.Wsl.ComposeRepoUrl,
            "Deleting the repo must not lose the URL needed to clone it again.");
        Assert.IsTrue(logs.Any(l => l.Contains("corrupt-", StringComparison.Ordinal)),
            "The op log must tell the operator where the archived copy went.");
    }

    [TestMethod]
    public async Task HandleStartDeleteAsync_MissingDir_IsNoOpSuccess()
    {
        Directory.Delete(_composeDir);
        _projectStore.Add(Project());

        var result = await ComposeRepoRouter.HandleStartDeleteAsync(
            "p1", _projectStore, _buildStore, NewService(), _bus);

        Assert.AreEqual(StatusCodes.Status202Accepted, StatusOf(result));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<List<string>> WaitForOpAsync(string opId)
    {
        var reader = _bus.Subscribe(opId);   // buffered events are replayed to late subscribers
        var payloads = new List<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            while (await reader.WaitToReadAsync(cts.Token))
                while (reader.TryRead(out var msg)) payloads.Add(msg);
        }
        catch (OperationCanceledException)
        {
            Assert.Fail("Background compose operation did not finish in time.");
        }
        return payloads;
    }

    private static (int Status, string Body) Execute(IResult result)
    {
        var ctx = new DefaultHttpContext();
        ctx.RequestServices = new ServiceCollection().AddLogging().AddProblemDetails().BuildServiceProvider();
        ctx.Response.Body = new MemoryStream();
        result.ExecuteAsync(ctx).GetAwaiter().GetResult();
        ctx.Response.Body.Position = 0;
        var body = new StreamReader(ctx.Response.Body).ReadToEnd();
        return (ctx.Response.StatusCode, body);
    }

    private static int StatusOf(IResult result) => Execute(result).Status;

    private static JsonElement BodyOf(IResult result) =>
        JsonDocument.Parse(Execute(result).Body).RootElement.Clone();

    private sealed class InMemoryProjectStore : IProjectStore
    {
        private readonly Dictionary<string, ProjectConfig> _projects = new();
        public ProjectConfig? Get(string id) => _projects.GetValueOrDefault(id);
        public void Add(ProjectConfig p) => _projects[p.Id] = p;
        public int Count => _projects.Count;
        public Task<List<ProjectConfig>> GetAllAsync() => Task.FromResult(_projects.Values.ToList());
        public Task<ProjectConfig?> GetByIdAsync(string id) => Task.FromResult(Get(id));
        public Task<ProjectConfig?> GetByNameAsync(string name) =>
            Task.FromResult(_projects.Values.FirstOrDefault(p => p.Name == name));
        public Task SaveAsync(ProjectConfig project)
        {
            _projects[project.Id] = project;
            return Task.CompletedTask;
        }
        public Task DeleteAsync(string id)
        {
            _projects.Remove(id);
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryBuildStore : IBuildStore
    {
        private readonly Dictionary<string, BuildRecord> _builds = new();
        public void Add(BuildRecord r) => _builds[r.Id] = r;
        public int Count => _builds.Count;
        public Task SaveAsync(BuildRecord record)
        {
            _builds[record.Id] = record;
            return Task.CompletedTask;
        }
        public Task<BuildRecord?> GetByIdAsync(string id) => Task.FromResult(_builds.GetValueOrDefault(id));
        public Task<List<BuildRecord>> QueryAsync(string? projectId, string? status, DateTime? from,
            DateTime? to, string? gitTag, int page, int pageSize)
        {
            var wanted = (status ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => Enum.TryParse<BuildStatus>(s, out var v) ? v : (BuildStatus?)null)
                .Where(v => v is not null).Select(v => v!.Value).ToHashSet();
            return Task.FromResult(_builds.Values
                .Where(b => projectId is null || b.ProjectId == projectId)
                .Where(b => wanted.Count == 0 || wanted.Contains(b.Status))
                .Skip((page - 1) * pageSize).Take(pageSize).ToList());
        }
        public Task<int> CountQueryAsync(string? projectId, string? status, DateTime? from,
            DateTime? to, string? gitTag) => Task.FromResult(0);
        public Task MarkInterruptedAsync() => Task.CompletedTask;
    }

    private sealed class StubProcessRunner : IProcessRunner
    {
        public string WindowsDir { get; set; } = "";
        public string CloneUrl { get; set; } = "";
        public bool IsGitRepo { get; set; } = true;
        public List<(string Executable, string[] Args, string? Stdin)> Calls { get; } = [];

        public Task<ProcessResult> RunAsync(
            string executable, string[] args, string? workingDir,
            Func<string, Task>? onOutput = null, Func<string, Task>? onError = null,
            CancellationToken ct = default, IReadOnlyDictionary<string, string>? envOverride = null,
            TimeSpan? timeout = null, string? stdin = null)
        {
            Calls.Add((executable, args, stdin));
            ProcessResult R(int code, string o, string e) => new(code, o, e, TimeSpan.Zero);

            if (executable == "wsl" && args.Contains("wslpath")) return Task.FromResult(R(0, WindowsDir, ""));
            if (args.Contains("--is-inside-work-tree"))
                return Task.FromResult(IsGitRepo ? R(0, "true", "") : R(128, "", "fatal: not a git repository"));
            if (args.Contains("get-url")) return Task.FromResult(R(0, CloneUrl, ""));
            if (args.Contains("fsck")) return Task.FromResult(R(0, "", ""));
            if (args.Contains("clone"))
            {
                Directory.CreateDirectory(WindowsDir);
                IsGitRepo = true;
                return Task.FromResult(R(0, "", ""));
            }
            return Task.FromResult(R(0, "", ""));
        }
    }
}
