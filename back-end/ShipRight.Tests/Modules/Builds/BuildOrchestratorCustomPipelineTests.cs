using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Builds;
using ShipRight.Modules.Projects;
using ShipRight.Modules.Resources;
using ShipRight.Modules.Resources.Models;
using ShipRight.Modules.Resources.Stores;
using ShipRight.Shared.Events;
using ShipRight.Shared.ProcessRunner;
using ShipRight.Shared.SshRunner;

namespace ShipRight.Tests.Modules.Builds;

[TestClass]
public class BuildOrchestratorCustomPipelineTests
{
    private FakeBuildStore _buildStore = null!;
    private FakeProjectStore _projectStore = null!;
    private FakePipelineResourceStore _pipelineStore = null!;
    private FakeScriptResourceStore _scriptStore = null!;
    private BuildEventBus _bus = null!;
    private FakeProcessRunner _runner = null!;
    private FakeSshRunner _ssh = null!;
    private ResourceResolutionService _resourceResolution = null!;
    private ScriptExecutor _scriptExecutor = null!;
    private TestableBuildOrchestrator _orchestrator = null!;

    [TestInitialize]
    public void Setup()
    {
        _buildStore = new FakeBuildStore();
        _projectStore = new FakeProjectStore();
        _pipelineStore = new FakePipelineResourceStore();
        _scriptStore = new FakeScriptResourceStore();
        _bus = new BuildEventBus();
        _runner = new FakeProcessRunner();
        _ssh = new FakeSshRunner();
        _resourceResolution = new ResourceResolutionService(
            new FakeDockerRegistryResourceStore(), _scriptStore);
        _scriptExecutor = new ScriptExecutor(_runner);
        _orchestrator = new TestableBuildOrchestrator(
            _buildStore, _projectStore, _bus, _runner, _ssh,
            _resourceResolution, _pipelineStore, _scriptStore, _scriptExecutor);
    }

    [TestMethod]
    public async Task StartAsync_WithPipelineResourceId_LooksUpPipeline()
    {
        var project = CreateProject("proj-1");
        _projectStore.Add(project);

        var pipeline = new PipelineResource
        {
            Id = Guid.NewGuid(),
            Name = "Test Pipeline",
            Scope = PipelineScope.Global,
            Steps =
            [
                new() { Type = PipelineStepType.Script, ScriptResourceId = Guid.NewGuid(), Label = "Pre-build" },
                new() { Type = PipelineStepType.Build, Label = "Build" },
            ],
        };
        _pipelineStore.Add(pipeline);

        var script = new ScriptResource
        {
            Id = pipeline.Steps[0].ScriptResourceId!.Value,
            Name = "Pre-build script",
            Content = "echo hello",
            Platform = ScriptPlatform.Bash,
            Target = ExecutionTarget.Local,
            Scope = PipelineScope.Global,
        };
        _scriptStore.Add(script);

        _runner.SetDefaultResult(new ProcessResult(0, "ok", "", TimeSpan.Zero));

        var request = new StartBuildRequest("proj-1",
            [new("api", "1.0.0")], pipeline.Id.ToString());

        var record = await _orchestrator.StartAsync(request);

        Assert.IsNotNull(record);
        Assert.AreEqual("proj-1", record.ProjectId);
        Assert.AreEqual(BuildStatus.Running, record.Status);

        await WaitForBuildCompletion(record.Id);
        var saved = await _buildStore.GetByIdAsync(record.Id);
        Assert.IsNotNull(saved);
        Assert.IsTrue(saved.SucceededSteps.Contains("Pre-build"), "Pre-build step should be in SucceededSteps");
        Assert.IsTrue(saved.SucceededSteps.Contains("DockerBuild"), "DockerBuild step should be in SucceededSteps");
    }

    [TestMethod]
    public async Task StartAsync_WithPipelineResourceId_ExecutesScriptStep()
    {
        var project = CreateProject("proj-2");
        _projectStore.Add(project);

        var scriptId = Guid.NewGuid();
        var pipeline = new PipelineResource
        {
            Id = Guid.NewGuid(),
            Name = "Script Only",
            Scope = PipelineScope.Global,
            Steps =
            [
                new() { Type = PipelineStepType.Script, ScriptResourceId = scriptId, Label = "Run checks" },
            ],
        };
        _pipelineStore.Add(pipeline);

        var script = new ScriptResource
        {
            Id = scriptId,
            Name = "Run checks",
            Content = "echo 'running checks'",
            Platform = ScriptPlatform.Bash,
            Target = ExecutionTarget.Local,
            Scope = PipelineScope.Global,
        };
        _scriptStore.Add(script);

        _runner.SetDefaultResult(new ProcessResult(0, "running checks", "", TimeSpan.FromSeconds(1)));

        var request = new StartBuildRequest("proj-2",
            [new("api", "2.0.0")], pipeline.Id.ToString());

        var record = await _orchestrator.StartAsync(request);
        await WaitForBuildCompletion(record.Id);

        var saved = await _buildStore.GetByIdAsync(record.Id);
        Assert.IsNotNull(saved);
        Assert.AreEqual(BuildStatus.ImageBuilt, saved.Status);
        Assert.IsTrue(saved.SucceededSteps.Contains("Run checks"));
        Assert.IsNotNull(_runner.LastExecutable, "Script executor should have invoked a shell");
    }

    [TestMethod]
    public async Task StartAsync_WithPipelineResourceId_ScriptFails_ContinueOnErrorFalse_Throws()
    {
        var project = CreateProject("proj-3");
        _projectStore.Add(project);

        var scriptId = Guid.NewGuid();
        var pipeline = new PipelineResource
        {
            Id = Guid.NewGuid(),
            Name = "Failing Script",
            Scope = PipelineScope.Global,
            Steps =
            [
                new() { Type = PipelineStepType.Script, ScriptResourceId = scriptId, Label = "Failing step", ContinueOnError = false },
            ],
        };
        _pipelineStore.Add(pipeline);

        var script = new ScriptResource
        {
            Id = scriptId,
            Name = "Failing step",
            Content = "exit 1",
            Platform = ScriptPlatform.Bash,
            Target = ExecutionTarget.Local,
            Scope = PipelineScope.Global,
        };
        _scriptStore.Add(script);

        _runner.SetDefaultResult(new ProcessResult(1, "", "error", TimeSpan.Zero));

        var request = new StartBuildRequest("proj-3",
            [new("api", "3.0.0")], pipeline.Id.ToString());

        var record = await _orchestrator.StartAsync(request);
        await WaitForBuildCompletion(record.Id);

        var saved = await _buildStore.GetByIdAsync(record.Id);
        Assert.IsNotNull(saved);
        Assert.AreEqual(BuildStatus.BuildFailed, saved.Status);
    }

    [TestMethod]
    public async Task StartAsync_WithPipelineResourceId_ScriptFails_ContinueOnErrorTrue_Continues()
    {
        var project = CreateProject("proj-4");
        _projectStore.Add(project);

        var scriptId = Guid.NewGuid();
        var pipeline = new PipelineResource
        {
            Id = Guid.NewGuid(),
            Name = "Resilient Pipeline",
            Scope = PipelineScope.Global,
            Steps =
            [
                new() { Type = PipelineStepType.Script, ScriptResourceId = scriptId, Label = "Failable", ContinueOnError = true },
                new() { Type = PipelineStepType.Build, Label = "Build" },
            ],
        };
        _pipelineStore.Add(pipeline);

        var script = new ScriptResource
        {
            Id = scriptId,
            Name = "Failable",
            Content = "exit 1",
            Platform = ScriptPlatform.Bash,
            Target = ExecutionTarget.Local,
            Scope = PipelineScope.Global,
        };
        _scriptStore.Add(script);

        _runner.SetDefaultResult(new ProcessResult(1, "", "fail", TimeSpan.Zero));

        var request = new StartBuildRequest("proj-4",
            [new("api", "4.0.0")], pipeline.Id.ToString());

        var record = await _orchestrator.StartAsync(request);
        await WaitForBuildCompletion(record.Id);

        var saved = await _buildStore.GetByIdAsync(record.Id);
        Assert.IsNotNull(saved);
        Assert.AreEqual(BuildStatus.ImageBuilt, saved.Status);
        Assert.IsTrue(saved.SucceededSteps.Contains("DockerBuild"), "Build step should have run after continue-on-error");
    }

    [TestMethod]
    public async Task StartAsync_WithoutPipelineResourceId_RunsDefaultPipeline()
    {
        var project = CreateProject("proj-5");
        _projectStore.Add(project);

        _runner.SetDefaultResult(new ProcessResult(0, "ok", "", TimeSpan.Zero));

        var request = new StartBuildRequest("proj-5",
            [new("api", "5.0.0")]);

        var record = await _orchestrator.StartAsync(request);

        Assert.IsNotNull(record);
        Assert.AreEqual("proj-5", record.ProjectId);
        Assert.AreEqual(BuildStatus.Running, record.Status);
    }

    [TestMethod]
    public async Task StartAsync_PipelineWithBuildPushDeploySteps_ExecutesInOrder()
    {
        var project = CreateProject("proj-6");
        _projectStore.Add(project);

        var pipeline = new PipelineResource
        {
            Id = Guid.NewGuid(),
            Name = "Full Pipeline",
            Scope = PipelineScope.Global,
            Steps =
            [
                new() { Type = PipelineStepType.Build, Label = "Build" },
                new() { Type = PipelineStepType.Push, Label = "Push" },
            ],
        };
        _pipelineStore.Add(pipeline);

        _runner.SetDefaultResult(new ProcessResult(0, "ok", "", TimeSpan.Zero));

        var request = new StartBuildRequest("proj-6",
            [new("api", "6.0.0")], pipeline.Id.ToString());

        var record = await _orchestrator.StartAsync(request);
        await WaitForBuildCompletion(record.Id);

        var saved = await _buildStore.GetByIdAsync(record.Id);
        Assert.IsNotNull(saved);
        Assert.IsTrue(saved.SucceededSteps.Contains("DockerBuild"));
    }

    [TestMethod]
    public async Task StartAsync_PipelineResourceNotFound_RunsDefaultPipeline()
    {
        var project = CreateProject("proj-7");
        _projectStore.Add(project);

        _runner.SetDefaultResult(new ProcessResult(0, "ok", "", TimeSpan.Zero));

        var request = new StartBuildRequest("proj-7",
            [new("api", "7.0.0")], Guid.NewGuid().ToString());

        var record = await _orchestrator.StartAsync(request);

        Assert.IsNotNull(record);
        Assert.AreEqual("proj-7", record.ProjectId);
        Assert.AreEqual(BuildStatus.Running, record.Status);
    }

    private static ProjectConfig CreateProject(string id)
    {
        var versionFile = Path.Combine(Path.GetTempPath(), $"{id}_version.txt");
        var buildContextPath = Path.Combine(Path.GetTempPath(), $"{id}_build");
        var wslDir = Path.Combine(Path.GetTempPath(), $"{id}_wsl");

        File.WriteAllText(versionFile, "1.0.0");
        Directory.CreateDirectory(buildContextPath);
        Directory.CreateDirectory(wslDir);

        return new()
        {
            Id = id,
            Name = $"Project {id}",
            Services =
            [
                new()
                {
                    Name = "api",
                    VersionFilePath = versionFile,
                    BuildContextPath = buildContextPath,
                    DockerImageName = $"{id}/api",
                },
            ],
            GitRepos = [],
            Wsl = new() { WorkingDir = wslDir },
            Server = new() { Host = "localhost", Username = "test", SshKeyPath = "", RemoteWorkingDir = "/app", DeployMode = DeployMode.GitScript },
        };
    }

    private async Task WaitForBuildCompletion(string buildId, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var record = await _buildStore.GetByIdAsync(buildId);
            if (record is not null && record.Status is not BuildStatus.Running and not BuildStatus.Pending)
                return;
            await Task.Delay(50);
        }
    }

    private sealed class TestableBuildOrchestrator : BuildOrchestrator
    {
        public TestableBuildOrchestrator(
            IBuildStore buildStore, IProjectStore projectStore, BuildEventBus bus,
            IProcessRunner runner, ISshRunner ssh, ResourceResolutionService resourceResolution,
            IPipelineResourceStore pipelineStore, IScriptResourceStore scriptStore,
            ScriptExecutor scriptExecutor)
            : base(buildStore, projectStore, bus, runner, ssh, resourceResolution, pipelineStore, scriptStore, scriptExecutor) { }

        protected override async Task RunDockerBuildAsync(PipelineContext ctx, BuildRecord record,
            ProjectConfig project, Func<Task> save, bool useBuildKit = false, CancellationToken ct = default)
        {
            record.Status = BuildStatus.ImageBuilt;
            record.SucceededSteps.Add("DockerBuild");
            await save();
        }
    }

    #region Fakes

    private sealed class FakeBuildStore : IBuildStore
    {
        private readonly Dictionary<string, BuildRecord> _records = new();
        public int Count => _records.Count;
        public Task SaveAsync(BuildRecord record) { _records[record.Id] = record; return Task.CompletedTask; }
        public Task<BuildRecord?> GetByIdAsync(string id) =>
            Task.FromResult(_records.TryGetValue(id, out var r) ? r : null);
        public Task<List<BuildRecord>> QueryAsync(string? projectId, string? status, DateTime? from, DateTime? to, string? gitTag, int page, int pageSize) =>
            Task.FromResult(new List<BuildRecord>());
        public Task<int> CountQueryAsync(string? projectId, string? status, DateTime? from, DateTime? to, string? gitTag) =>
            Task.FromResult(0);
        public Task MarkInterruptedAsync() => Task.CompletedTask;
    }

    private sealed class FakeProjectStore : IProjectStore
    {
        private readonly Dictionary<string, ProjectConfig> _projects = new();
        public int Count => _projects.Count;
        public void Add(ProjectConfig p) => _projects[p.Id] = p;
        public Task<List<ProjectConfig>> GetAllAsync() =>
            Task.FromResult(_projects.Values.ToList());
        public Task<ProjectConfig?> GetByIdAsync(string id) =>
            Task.FromResult(_projects.TryGetValue(id, out var p) ? p : null);
        public Task<ProjectConfig?> GetByNameAsync(string name) =>
            Task.FromResult(_projects.Values.FirstOrDefault(p => p.Name == name));
        public Task SaveAsync(ProjectConfig project) { _projects[project.Id] = project; return Task.CompletedTask; }
        public Task DeleteAsync(string id) { _projects.Remove(id); return Task.CompletedTask; }
    }

    private sealed class FakePipelineResourceStore : IPipelineResourceStore
    {
        private readonly Dictionary<Guid, PipelineResource> _pipelines = new();
        public int Count => _pipelines.Count;
        public void Add(PipelineResource p) => _pipelines[p.Id] = p;
        public Task<List<PipelineResource>> GetAllAsync() =>
            Task.FromResult(_pipelines.Values.ToList());
        public Task<List<PipelineResource>> GetGlobalAsync() =>
            Task.FromResult(_pipelines.Values.Where(p => p.Scope == PipelineScope.Global).ToList());
        public Task<List<PipelineResource>> GetByProjectAsync(Guid projectId) =>
            Task.FromResult(_pipelines.Values.Where(p => p.Scope == PipelineScope.Project && p.ProjectId == projectId).ToList());
        public Task<PipelineResource?> GetByIdAsync(Guid id) =>
            Task.FromResult(_pipelines.TryGetValue(id, out var p) ? p : null);
        public Task SaveAsync(PipelineResource resource) { _pipelines[resource.Id] = resource; return Task.CompletedTask; }
        public Task DeleteAsync(Guid id) { _pipelines.Remove(id); return Task.CompletedTask; }
    }

    private sealed class FakeScriptResourceStore : IScriptResourceStore
    {
        private readonly Dictionary<Guid, ScriptResource> _scripts = new();
        public int Count => _scripts.Count;
        public void Add(ScriptResource s) => _scripts[s.Id] = s;
        public Task<List<ScriptResource>> GetAllAsync() =>
            Task.FromResult(_scripts.Values.ToList());
        public Task<List<ScriptResource>> GetGlobalAsync() =>
            Task.FromResult(_scripts.Values.Where(s => s.Scope == PipelineScope.Global).ToList());
        public Task<List<ScriptResource>> GetByProjectAsync(Guid projectId) =>
            Task.FromResult(_scripts.Values.Where(s => s.Scope == PipelineScope.Project && s.ProjectId == projectId).ToList());
        public Task<ScriptResource?> GetByIdAsync(Guid id) =>
            Task.FromResult(_scripts.TryGetValue(id, out var s) ? s : null);
        public Task<ScriptResource?> GetByNameAsync(string name) =>
            Task.FromResult(_scripts.Values.FirstOrDefault(s => s.Name == name));
        public Task SaveAsync(ScriptResource resource) { _scripts[resource.Id] = resource; return Task.CompletedTask; }
        public Task DeleteAsync(Guid id) { _scripts.Remove(id); return Task.CompletedTask; }
    }

    private sealed class FakeDockerRegistryResourceStore : IDockerRegistryResourceStore
    {
        public int Count => 0;
        public Task<List<DockerRegistryResource>> GetAllAsync() => Task.FromResult(new List<DockerRegistryResource>());
        public Task<DockerRegistryResource?> GetByIdAsync(Guid id) => Task.FromResult<DockerRegistryResource?>(null);
        public Task<DockerRegistryResource?> GetByNameAsync(string name) => Task.FromResult<DockerRegistryResource?>(null);
        public Task SaveAsync(DockerRegistryResource resource) => Task.CompletedTask;
        public Task DeleteAsync(Guid id) => Task.CompletedTask;
    }

    private sealed class FakeProcessRunner : IProcessRunner
    {
        private ProcessResult _defaultResult = new(0, "", "", TimeSpan.Zero);
        private Func<string, string[], ProcessResult>? _resultFactory;

        public string? LastExecutable { get; private set; }
        public string[]? LastArgs { get; private set; }

        public void SetDefaultResult(ProcessResult result) => _defaultResult = result;
        public void SetResultFactory(Func<string, string[], ProcessResult> factory) => _resultFactory = factory;

        public Task<ProcessResult> RunAsync(
            string executable, string[] args, string? workingDir,
            Func<string, Task>? onOutput = null, Func<string, Task>? onError = null,
            CancellationToken ct = default, IReadOnlyDictionary<string, string>? envOverride = null)
        {
            LastExecutable = executable;
            LastArgs = args;
            var result = _resultFactory?.Invoke(executable, args) ?? _defaultResult;
            return Task.FromResult(result);
        }
    }

    private sealed class FakeSshRunner : ISshRunner
    {
        public Task<int> RunAsync(string host, string username, string keyPath, string command,
            Func<string, Task>? onOutput = null, Func<string, Task>? onStderr = null,
            CancellationToken ct = default) => Task.FromResult(0);
    }

    #endregion
}
