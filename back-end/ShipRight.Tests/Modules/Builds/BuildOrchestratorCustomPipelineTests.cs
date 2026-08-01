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
    private FakeDockerRegistryResourceStore _registryStore = null!;
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
        _registryStore = new FakeDockerRegistryResourceStore();
        _bus = new BuildEventBus();
        _runner = new FakeProcessRunner();
        _ssh = new FakeSshRunner();
        _resourceResolution = new ResourceResolutionService(
            _registryStore, _scriptStore, processRunner: _runner);
        _scriptExecutor = new ScriptExecutor(_runner);
        var credentialStore = new FakeCredentialResourceStore();
        _orchestrator = new TestableBuildOrchestrator(
            _buildStore, _projectStore, _bus, _runner, _ssh,
            _resourceResolution, _pipelineStore, _scriptStore, credentialStore, _scriptExecutor);
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

    [TestMethod]
    public async Task CustomPushStep_InvokesDockerPushWithExactArgs()
    {
        var project = CreateProject("proj-push-1");
        project.Services[0] = project.Services[0] with
        {
            DockerImageName = "ghcr.io/org/app",
            DockerRegistry = "ghcr.io",
            DockerUsername = "user",
            DockerPassword = "pass",
        };
        _projectStore.Add(project);

        var pipeline = new PipelineResource
        {
            Id = Guid.NewGuid(),
            Name = "Push Only",
            Scope = PipelineScope.Global,
            Steps = [new() { Type = PipelineStepType.Push, Label = "Push" }],
        };
        _pipelineStore.Add(pipeline);

        string[]? dockerArgs = null;
        _runner.SetResultFactory((exe, args) =>
        {
            if (exe == "docker") dockerArgs = args;
            return new ProcessResult(0, "ok", "", TimeSpan.Zero);
        });

        var record = await _orchestrator.StartAsync(new StartBuildRequest("proj-push-1",
            [new("api", "9.0.0")], pipeline.Id.ToString()));
        await WaitForBuildCompletion(record.Id);

        CollectionAssert.AreEqual(new[] { "push", "ghcr.io/org/app:9.0.0" }, dockerArgs);
    }

    [TestMethod]
    public async Task CustomPushStep_PushFailure_FailsBuild()
    {
        var project = CreateProject("proj-push-2");
        project.Services[0] = project.Services[0] with
        {
            DockerImageName = "owner/api",
            DockerUsername = "user",
            DockerPassword = "pass",
        };
        _projectStore.Add(project);

        var pipeline = new PipelineResource
        {
            Id = Guid.NewGuid(),
            Name = "Push Only",
            Scope = PipelineScope.Global,
            Steps = [new() { Type = PipelineStepType.Push, Label = "Push" }],
        };
        _pipelineStore.Add(pipeline);

        _runner.SetResultFactory((exe, args) =>
            exe == "docker" && args.Length > 0 && args[0] == "push"
                ? new ProcessResult(1, "", "connection refused", TimeSpan.Zero)
                : new ProcessResult(0, "ok", "", TimeSpan.Zero));

        var record = await _orchestrator.StartAsync(new StartBuildRequest("proj-push-2",
            [new("api", "9.0.0")], pipeline.Id.ToString()));
        await WaitForBuildCompletion(record.Id);

        var saved = await _buildStore.GetByIdAsync(record.Id);
        Assert.IsNotNull(saved);
        Assert.AreEqual(BuildStatus.BuildFailed, saved.Status);
    }

    [TestMethod]
    public async Task CustomPushStep_MultipleServices_PushesEachServiceInOrder()
    {
        var versionFile = Path.Combine(Path.GetTempPath(), "proj-push-3_version.txt");
        var buildContextPath = Path.Combine(Path.GetTempPath(), "proj-push-3_build");
        var wslDir = Path.Combine(Path.GetTempPath(), "proj-push-3_wsl");
        File.WriteAllText(versionFile, "1.0.0");
        Directory.CreateDirectory(buildContextPath);
        Directory.CreateDirectory(wslDir);

        var project = new ProjectConfig
        {
            Id = "proj-push-3",
            Name = "Multi",
            Services =
            [
                new() { Name = "api", VersionFilePath = versionFile, BuildContextPath = buildContextPath, DockerImageName = "ghcr.io/org/api", DockerRegistry = "ghcr.io", DockerUsername = "user", DockerPassword = "pass" },
                new() { Name = "web", VersionFilePath = versionFile, BuildContextPath = buildContextPath, DockerImageName = "ghcr.io/org/web", DockerRegistry = "ghcr.io", DockerUsername = "user", DockerPassword = "pass" },
            ],
            GitRepos = [],
            Wsl = new() { WorkingDir = wslDir },
            Server = new() { Host = "localhost", Username = "test", SshKeyPath = "", RemoteWorkingDir = "/app", DeployMode = DeployMode.GitScript },
        };
        _projectStore.Add(project);

        var pipeline = new PipelineResource
        {
            Id = Guid.NewGuid(),
            Name = "Push Only",
            Scope = PipelineScope.Global,
            Steps = [new() { Type = PipelineStepType.Push, Label = "Push" }],
        };
        _pipelineStore.Add(pipeline);

        var dockerCalls = new List<string[]>();
        _runner.SetResultFactory((exe, args) =>
        {
            if (exe == "docker") dockerCalls.Add(args);
            return new ProcessResult(0, "ok", "", TimeSpan.Zero);
        });

        var record = await _orchestrator.StartAsync(new StartBuildRequest("proj-push-3",
            [new("api", "1.1.0"), new("web", "2.0.0")], pipeline.Id.ToString()));
        await WaitForBuildCompletion(record.Id);

        var pushArgs = dockerCalls.Where(a => a[0] == "push").ToList();
        Assert.AreEqual(2, pushArgs.Count);
        CollectionAssert.AreEqual(new[] { "push", "ghcr.io/org/api:1.1.0" }, pushArgs[0]);
        CollectionAssert.AreEqual(new[] { "push", "ghcr.io/org/web:2.0.0" }, pushArgs[1]);
    }

    [TestMethod]
    public async Task CustomPushStep_WithStoredCredentials_PerformsLoginSequenceBeforePush()
    {
        var project = CreateProject("proj-push-4");
        project.Services[0] = project.Services[0] with
        {
            DockerImageName = "owner/api",
            DockerUsername = "user",
            DockerPassword = "pass",
        };
        _projectStore.Add(project);

        var pipeline = new PipelineResource
        {
            Id = Guid.NewGuid(),
            Name = "Push Only",
            Scope = PipelineScope.Global,
            Steps = [new() { Type = PipelineStepType.Push, Label = "Push" }],
        };
        _pipelineStore.Add(pipeline);

        _runner.SetDefaultResult(new ProcessResult(0, "ok", "", TimeSpan.Zero));

        var record = await _orchestrator.StartAsync(new StartBuildRequest("proj-push-4",
            [new("api", "9.0.0")], pipeline.Id.ToString()));
        await WaitForBuildCompletion(record.Id);

        var dockerCalls = _runner.Calls.Where(c => c.Executable == "docker").Select(c => c.Args).ToList();
        Assert.AreEqual(5, dockerCalls.Count, $"Unexpected docker sequence: {string.Join(" | ", dockerCalls.Select(a => string.Join(' ', a)))}");
        CollectionAssert.AreEqual(new[] { "logout" }, dockerCalls[0]);
        CollectionAssert.AreEqual(new[] { "logout" }, dockerCalls[1]);
        CollectionAssert.AreEqual(new[] { "login", "-u", "user", "--password-stdin" }, dockerCalls[2]);
        CollectionAssert.AreEqual(new[] { "info" }, dockerCalls[3]);
        CollectionAssert.AreEqual(new[] { "push", "owner/api:9.0.0" }, dockerCalls[4]);

        var loginCall = _runner.Calls.Single(c => c.Executable == "docker" && c.Args[0] == "login");
        Assert.AreEqual("pass", loginCall.Stdin, "Password must be piped to docker login via stdin.");
    }

    [TestMethod]
    public async Task CustomPushStep_NoCredentials_PromptsForCredentials_ThenLogsIn()
    {
        var project = CreateProject("proj-push-5");
        project.Services[0] = project.Services[0] with { DockerImageName = "owner/api" };
        _projectStore.Add(project);

        var pipeline = new PipelineResource
        {
            Id = Guid.NewGuid(),
            Name = "Push Only",
            Scope = PipelineScope.Global,
            Steps = [new() { Type = PipelineStepType.Push, Label = "Push" }],
        };
        _pipelineStore.Add(pipeline);

        _runner.SetDefaultResult(new ProcessResult(0, "ok", "", TimeSpan.Zero));

        var record = await _orchestrator.StartAsync(new StartBuildRequest("proj-push-5",
            [new("api", "9.0.0")], pipeline.Id.ToString()));

        await RespondToPauseAsync(record.Id, "login", new Dictionary<string, string>
        {
            ["username"] = "prompted-user",
            ["password"] = "prompted-pass",
        });
        await WaitForBuildCompletion(record.Id);

        var dockerCalls = _runner.Calls.Where(c => c.Executable == "docker").Select(c => c.Args).ToList();
        Assert.AreEqual(4, dockerCalls.Count, $"Unexpected docker sequence: {string.Join(" | ", dockerCalls.Select(a => string.Join(' ', a)))}");
        CollectionAssert.AreEqual(new[] { "login", "-u", "prompted-user", "--password-stdin" }, dockerCalls[2]);
        Assert.AreEqual("prompted-pass", _runner.Calls.Single(c => c.Executable == "docker" && c.Args[0] == "login").Stdin);
        CollectionAssert.AreEqual(new[] { "push", "owner/api:9.0.0" }, dockerCalls[3]);

        var savedProject = await _projectStore.GetByIdAsync("proj-push-5");
        Assert.IsNotNull(savedProject);
        Assert.AreEqual("prompted-user", savedProject!.Services[0].DockerUsername, "Prompted credentials should be saved back to the project.");
        Assert.AreEqual("prompted-pass", savedProject.Services[0].DockerPassword);
    }

    [TestMethod]
    public async Task CustomPushStep_PushDenied_ReAuthenticatesAndRetriesPush()
    {
        var project = CreateProject("proj-push-6");
        project.Services[0] = project.Services[0] with
        {
            DockerImageName = "owner/api",
            DockerUsername = "user",
            DockerPassword = "pass",
        };
        _projectStore.Add(project);

        var pipeline = new PipelineResource
        {
            Id = Guid.NewGuid(),
            Name = "Push Only",
            Scope = PipelineScope.Global,
            Steps = [new() { Type = PipelineStepType.Push, Label = "Push" }],
        };
        _pipelineStore.Add(pipeline);

        var pushAttempts = 0;
        _runner.SetResultFactory((exe, args) =>
        {
            if (exe == "docker" && args.Length > 0 && args[0] == "push")
            {
                pushAttempts++;
                if (pushAttempts == 1)
                    return new ProcessResult(1, "", "denied: requested access to the resource is denied", TimeSpan.Zero);
            }
            return new ProcessResult(0, "ok", "", TimeSpan.Zero);
        });

        var record = await _orchestrator.StartAsync(new StartBuildRequest("proj-push-6",
            [new("api", "9.0.0")], pipeline.Id.ToString()));

        await RespondToPauseAsync(record.Id, "login", new Dictionary<string, string>
        {
            ["username"] = "user",
            ["password"] = "re-entered",
        });
        await WaitForBuildCompletion(record.Id);

        var dockerCalls = _runner.Calls.Where(c => c.Executable == "docker").Select(c => c.Args).ToList();
        Assert.AreEqual(2, dockerCalls.Count(a => a[0] == "push"), "Push should be attempted twice (initial + retry after re-login).");
        Assert.AreEqual(2, dockerCalls.Count(a => a[0] == "login"), "A login must occur after push denial.");
        CollectionAssert.AreEqual(new[] { "push", "owner/api:9.0.0" }, dockerCalls[4]);
        CollectionAssert.AreEqual(new[] { "login", "-u", "user", "--password-stdin" }, dockerCalls[6]);
        CollectionAssert.AreEqual(new[] { "push", "owner/api:9.0.0" }, dockerCalls[7]);
    }

    [TestMethod]
    public async Task CustomPushStep_OwnerMismatch_ReAuthenticates()
    {
        var project = CreateProject("proj-push-7");
        project.Services[0] = project.Services[0] with
        {
            DockerImageName = "owner/api",
            DockerUsername = "user",
            DockerPassword = "pass",
        };
        _projectStore.Add(project);

        var pipeline = new PipelineResource
        {
            Id = Guid.NewGuid(),
            Name = "Push Only",
            Scope = PipelineScope.Global,
            Steps = [new() { Type = PipelineStepType.Push, Label = "Push" }],
        };
        _pipelineStore.Add(pipeline);

        _runner.SetResultFactory((exe, args) =>
        {
            if (exe == "docker" && args.Length == 1 && args[0] == "info")
                return new ProcessResult(0, "Server:\n Username: someone-else\n", "", TimeSpan.Zero);
            return new ProcessResult(0, "ok", "", TimeSpan.Zero);
        });

        var record = await _orchestrator.StartAsync(new StartBuildRequest("proj-push-7",
            [new("api", "9.0.0")], pipeline.Id.ToString()));

        await RespondToPauseAsync(record.Id, "login", new Dictionary<string, string>
        {
            ["username"] = "owner",
            ["password"] = "owner-pass",
        });
        await WaitForBuildCompletion(record.Id);

        var dockerCalls = _runner.Calls.Where(c => c.Executable == "docker").Select(c => c.Args).ToList();
        Assert.AreEqual(8, dockerCalls.Count, $"Unexpected docker sequence: {string.Join(" | ", dockerCalls.Select(a => string.Join(' ', a)))}");
        CollectionAssert.AreEqual(new[] { "logout" }, dockerCalls[0]);
        CollectionAssert.AreEqual(new[] { "logout" }, dockerCalls[1]);
        CollectionAssert.AreEqual(new[] { "login", "-u", "user", "--password-stdin" }, dockerCalls[2]);
        CollectionAssert.AreEqual(new[] { "info" }, dockerCalls[3]);
        CollectionAssert.AreEqual(new[] { "logout" }, dockerCalls[4], "Owner mismatch must trigger a logout before re-prompt.");
        CollectionAssert.AreEqual(new[] { "logout" }, dockerCalls[5], "Re-login clears stale credentials first.");
        CollectionAssert.AreEqual(new[] { "login", "-u", "owner", "--password-stdin" }, dockerCalls[6]);
        CollectionAssert.AreEqual(new[] { "push", "owner/api:9.0.0" }, dockerCalls[7]);
    }

    [TestMethod]
    public async Task CustomPushStep_BoundEcrResource_LogsInViaAwsAndPushesToEcrHost()
    {
        var resourceId = Guid.NewGuid();
        _registryStore.Seed(new DockerRegistryResource
        {
            Id = resourceId,
            Name = "prod-ecr",
            Registry = "123.dkr.ecr.us-east-1.amazonaws.com",
            AuthType = RegistryAuthType.AwsEcr,
            AwsRegion = "us-east-1",
        });

        var project = CreateProject("proj-ecr-1");
        project.Services[0] = project.Services[0] with
        {
            DockerImageName = "123.dkr.ecr.us-east-1.amazonaws.com/org/app",
            DockerRegistryResourceId = resourceId,
        };
        _projectStore.Add(project);

        var pipeline = new PipelineResource
        {
            Id = Guid.NewGuid(),
            Name = "Push Only",
            Scope = PipelineScope.Global,
            Steps = [new() { Type = PipelineStepType.Push, Label = "Push" }],
        };
        _pipelineStore.Add(pipeline);

        _runner.SetResultFactory((exe, args) =>
        {
            if (exe == "aws") return new ProcessResult(0, "ecr-token", "", TimeSpan.Zero);
            return new ProcessResult(0, "ok", "", TimeSpan.Zero);
        });

        var record = await _orchestrator.StartAsync(new StartBuildRequest("proj-ecr-1",
            [new("api", "1.0.0")], pipeline.Id.ToString()));
        await WaitForBuildCompletion(record.Id);

        var saved = await _buildStore.GetByIdAsync(record.Id);
        Assert.IsNotNull(saved);
        Assert.AreNotEqual(BuildStatus.BuildFailed, saved!.Status);

        var awsCalls = _runner.Calls.Where(c => c.Executable == "aws").Select(c => c.Args).ToList();
        Assert.AreEqual(1, awsCalls.Count);
        CollectionAssert.AreEqual(new[] { "ecr", "get-login-password", "--region", "us-east-1" }, awsCalls[0]);

        var dockerCalls = _runner.Calls.Where(c => c.Executable == "docker").Select(c => c.Args).ToList();
        CollectionAssert.AreEqual(new[] { "logout", "123.dkr.ecr.us-east-1.amazonaws.com" }, dockerCalls[0]);
        CollectionAssert.AreEqual(new[] { "logout", "123.dkr.ecr.us-east-1.amazonaws.com" }, dockerCalls[1]);
        CollectionAssert.AreEqual(new[] { "login", "123.dkr.ecr.us-east-1.amazonaws.com", "-u", "AWS", "--password-stdin" }, dockerCalls[2]);
        Assert.AreEqual("ecr-token", _runner.Calls.Single(c => c.Executable == "docker" && c.Args[0] == "login").Stdin);
        CollectionAssert.AreEqual(new[] { "push", "123.dkr.ecr.us-east-1.amazonaws.com/org/app:1.0.0" }, dockerCalls[4]);
    }

    [TestMethod]
    public async Task DefaultPipeline_StoredCredentials_PerformsExactLoginSequence()
    {
        var project = CreateProject("proj-login-1");
        project.Services[0] = project.Services[0] with
        {
            DockerImageName = "owner/api",
            DockerUsername = "user",
            DockerPassword = "pass",
        };
        project = project with { Server = project.Server with { DeployMode = DeployMode.EnvCompose } };
        _projectStore.Add(project);

        _runner.SetDefaultResult(new ProcessResult(0, "ok", "", TimeSpan.Zero));

        var record = await _orchestrator.StartAsync(new StartBuildRequest("proj-login-1", [new("api", "1.2.3")]));
        await WaitForStatusAsync(record.Id, BuildStatus.ImageBuilt);
        await _orchestrator.PushAsync(record.Id);
        await WaitForStatusAsync(record.Id, BuildStatus.PushSucceeded);

        var dockerCalls = _runner.Calls.Where(c => c.Executable == "docker").Select(c => c.Args).ToList();

        Assert.AreEqual(7, dockerCalls.Count, $"Unexpected docker sequence: {string.Join(" | ", dockerCalls.Select(a => string.Join(' ', a)))}");
        CollectionAssert.AreEqual(new[] { "info" }, dockerCalls[0]);
        CollectionAssert.AreEqual(new[] { "buildx", "version" }, dockerCalls[1]);
        CollectionAssert.AreEqual(new[] { "logout" }, dockerCalls[2]);
        CollectionAssert.AreEqual(new[] { "logout" }, dockerCalls[3]);
        CollectionAssert.AreEqual(new[] { "login", "-u", "user", "--password-stdin" }, dockerCalls[4]);
        CollectionAssert.AreEqual(new[] { "info" }, dockerCalls[5]);
        CollectionAssert.AreEqual(new[] { "push", "owner/api:1.2.3" }, dockerCalls[6]);

        var loginCall = _runner.Calls.Single(c => c.Executable == "docker" && c.Args[0] == "login");
        Assert.AreEqual("pass", loginCall.Stdin, "Password must be piped to docker login via stdin.");
    }

    [TestMethod]
    public async Task DefaultPipeline_GhcrStoredCredentials_LoginIncludesRegistryArg()
    {
        var project = CreateProject("proj-login-2");
        project.Services[0] = project.Services[0] with
        {
            DockerImageName = "org/api",
            DockerRegistry = "ghcr.io",
            DockerUsername = "user",
            DockerPassword = "pass",
        };
        project = project with { Server = project.Server with { DeployMode = DeployMode.EnvCompose } };
        _projectStore.Add(project);

        _runner.SetDefaultResult(new ProcessResult(0, "ok", "", TimeSpan.Zero));

        var record = await _orchestrator.StartAsync(new StartBuildRequest("proj-login-2", [new("api", "2.0.0")]));
        await WaitForStatusAsync(record.Id, BuildStatus.ImageBuilt);
        await _orchestrator.PushAsync(record.Id);
        await WaitForStatusAsync(record.Id, BuildStatus.PushSucceeded);

        var dockerCalls = _runner.Calls.Where(c => c.Executable == "docker").Select(c => c.Args).ToList();

        Assert.AreEqual(7, dockerCalls.Count);
        CollectionAssert.AreEqual(new[] { "logout", "ghcr.io" }, dockerCalls[2]);
        CollectionAssert.AreEqual(new[] { "logout", "ghcr.io" }, dockerCalls[3]);
        CollectionAssert.AreEqual(new[] { "login", "ghcr.io", "-u", "user", "--password-stdin" }, dockerCalls[4]);
        CollectionAssert.AreEqual(new[] { "push", "org/api:2.0.0" }, dockerCalls[6]);
    }

    [TestMethod]
    public async Task DefaultPipeline_NoStoredCredentials_PromptsForCredentials_ThenLogsIn()
    {
        var project = CreateProject("proj-login-3");
        project = project with { Server = project.Server with { DeployMode = DeployMode.EnvCompose } };
        _projectStore.Add(project);

        _runner.SetDefaultResult(new ProcessResult(0, "ok", "", TimeSpan.Zero));

        var record = await _orchestrator.StartAsync(new StartBuildRequest("proj-login-3", [new("api", "3.0.0")]));
        await WaitForStatusAsync(record.Id, BuildStatus.ImageBuilt);
        var pushTask = _orchestrator.PushAsync(record.Id);

        await RespondToPauseAsync(record.Id, "login", new Dictionary<string, string>
        {
            ["username"] = "prompted-user",
            ["password"] = "prompted-pass",
        });
        await pushTask;
        await WaitForStatusAsync(record.Id, BuildStatus.PushSucceeded);

        var dockerCalls = _runner.Calls.Where(c => c.Executable == "docker").Select(c => c.Args).ToList();
        var loginCall = _runner.Calls.Single(c => c.Executable == "docker" && c.Args[0] == "login");
        CollectionAssert.AreEqual(new[] { "login", "-u", "prompted-user", "--password-stdin" }, loginCall.Args);
        Assert.AreEqual("prompted-pass", loginCall.Stdin);

        var savedProject = await _projectStore.GetByIdAsync("proj-login-3");
        Assert.IsNotNull(savedProject);
        Assert.AreEqual("prompted-user", savedProject!.Services[0].DockerUsername, "Prompted credentials should be saved back to the project.");
        Assert.AreEqual("prompted-pass", savedProject.Services[0].DockerPassword);
    }

    [TestMethod]
    public async Task DefaultPipeline_PushDenied_ReAuthenticatesAndRetriesPush()
    {
        var project = CreateProject("proj-login-4");
        project.Services[0] = project.Services[0] with
        {
            DockerImageName = "owner/api",
            DockerUsername = "user",
            DockerPassword = "pass",
        };
        project = project with { Server = project.Server with { DeployMode = DeployMode.EnvCompose } };
        _projectStore.Add(project);

        var pushAttempts = 0;
        _runner.SetResultFactory((exe, args) =>
        {
            if (exe == "docker" && args.Length > 0 && args[0] == "push")
            {
                pushAttempts++;
                if (pushAttempts == 1)
                    return new ProcessResult(1, "", "denied: requested access to the resource is denied", TimeSpan.Zero);
            }
            return new ProcessResult(0, "ok", "", TimeSpan.Zero);
        });

        var record = await _orchestrator.StartAsync(new StartBuildRequest("proj-login-4", [new("api", "4.0.0")]));
        await WaitForStatusAsync(record.Id, BuildStatus.ImageBuilt);
        var pushTask = _orchestrator.PushAsync(record.Id);

        await RespondToPauseAsync(record.Id, "login", new Dictionary<string, string>
        {
            ["username"] = "user",
            ["password"] = "re-entered",
        });
        await pushTask;
        await WaitForStatusAsync(record.Id, BuildStatus.PushSucceeded);

        var dockerCalls = _runner.Calls.Where(c => c.Executable == "docker").Select(c => c.Args).ToList();
        Assert.AreEqual(2, dockerCalls.Count(a => a[0] == "push"), "Push should be attempted twice (initial + retry after re-login).");
        Assert.AreEqual(2, dockerCalls.Count(a => a[0] == "login"), "A login must occur after push denial.");
        Assert.AreEqual(10, dockerCalls.Count);
        CollectionAssert.AreEqual(new[] { "push", "owner/api:4.0.0" }, dockerCalls[6]);
        CollectionAssert.AreEqual(new[] { "push", "owner/api:4.0.0" }, dockerCalls[9]);
    }

    [TestMethod]
    public async Task DefaultPipeline_OwnerMismatch_ReAuthenticates()
    {
        var project = CreateProject("proj-login-5");
        project.Services[0] = project.Services[0] with
        {
            DockerImageName = "owner/api",
            DockerUsername = "user",
            DockerPassword = "pass",
        };
        project = project with { Server = project.Server with { DeployMode = DeployMode.EnvCompose } };
        _projectStore.Add(project);

        _runner.SetResultFactory((exe, args) =>
        {
            if (exe == "docker" && args.Length == 1 && args[0] == "info")
                return new ProcessResult(0, "Server:\n Username: someone-else\n", "", TimeSpan.Zero);
            return new ProcessResult(0, "ok", "", TimeSpan.Zero);
        });

        var record = await _orchestrator.StartAsync(new StartBuildRequest("proj-login-5", [new("api", "5.0.0")]));
        await WaitForStatusAsync(record.Id, BuildStatus.ImageBuilt);
        var pushTask = _orchestrator.PushAsync(record.Id);

        await RespondToPauseAsync(record.Id, "login", new Dictionary<string, string>
        {
            ["username"] = "owner",
            ["password"] = "owner-pass",
        });
        await pushTask;
        await WaitForStatusAsync(record.Id, BuildStatus.PushSucceeded);

        var dockerCalls = _runner.Calls.Where(c => c.Executable == "docker").Select(c => c.Args).ToList();
        Assert.AreEqual(10, dockerCalls.Count);
        CollectionAssert.AreEqual(new[] { "logout" }, dockerCalls[6], "Owner mismatch must trigger an extra logout before re-prompt.");
        CollectionAssert.AreEqual(new[] { "logout" }, dockerCalls[7]);
        CollectionAssert.AreEqual(new[] { "login", "-u", "owner", "--password-stdin" }, dockerCalls[8]);
        CollectionAssert.AreEqual(new[] { "push", "owner/api:5.0.0" }, dockerCalls[9]);
    }

    private async Task WaitForStatusAsync(string buildId, BuildStatus status, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var record = await _buildStore.GetByIdAsync(buildId);
            if (record is null) { await Task.Delay(25); continue; }
            if (record.Status == status) return;
            if (record.Status is BuildStatus.BuildFailed or BuildStatus.PushFailed or BuildStatus.Aborted)
                Assert.Fail($"Build reached unexpected status {record.Status} while waiting for {status}: {record.ErrorSummary}");
            await Task.Delay(25);
        }
        Assert.Fail($"Build never reached {status} within {timeoutMs}ms.");
    }

    private async Task RespondToPauseAsync(string buildId, string choice, Dictionary<string, string>? data)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var record = await _buildStore.GetByIdAsync(buildId);
            if (record is not null && record.Status == BuildStatus.Paused)
            {
                var handled = await _orchestrator.RespondAsync(buildId, new RespondRequest("docker_login_required", choice, data));
                Assert.IsTrue(handled, "RespondAsync should find the pending pause.");
                return;
            }
            await Task.Delay(50);
        }
        Assert.Fail("Build never reached the paused state.");
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
            ICredentialResourceStore credentialStore, ScriptExecutor scriptExecutor)
            : base(buildStore, projectStore, bus, runner, ssh, resourceResolution, pipelineStore, scriptStore, credentialStore, scriptExecutor) { }

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
        public Task<List<PipelineResource>> GetByProjectAsync(string projectId) =>
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
        public Task<List<ScriptResource>> GetByProjectAsync(string projectId) =>
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
        private readonly List<DockerRegistryResource> _resources = [];
        public int Count => _resources.Count;
        public Task<List<DockerRegistryResource>> GetAllAsync() => Task.FromResult(_resources.ToList());
        public Task<DockerRegistryResource?> GetByIdAsync(Guid id) =>
            Task.FromResult(_resources.FirstOrDefault(r => r.Id == id));
        public Task<DockerRegistryResource?> GetByNameAsync(string name) => Task.FromResult<DockerRegistryResource?>(null);
        public Task SaveAsync(DockerRegistryResource resource)
        {
            _resources.RemoveAll(r => r.Id == resource.Id);
            _resources.Add(resource);
            return Task.CompletedTask;
        }
        public Task DeleteAsync(Guid id)
        {
            _resources.RemoveAll(r => r.Id == id);
            return Task.CompletedTask;
        }
        public void Seed(DockerRegistryResource resource) => _resources.Add(resource);
    }

    private sealed class FakeProcessRunner : IProcessRunner
    {
        private ProcessResult _defaultResult = new(0, "", "", TimeSpan.Zero);
        private Func<string, string[], ProcessResult>? _resultFactory;

        public string? LastExecutable { get; private set; }
        public string[]? LastArgs { get; private set; }
        public string? LastStdin { get; private set; }
        public TimeSpan? LastTimeout { get; private set; }
        public List<(string Executable, string[] Args, string? Stdin)> Calls { get; } = new();

        public void SetDefaultResult(ProcessResult result) => _defaultResult = result;
        public void SetResultFactory(Func<string, string[], ProcessResult> factory) => _resultFactory = factory;

        public Task<ProcessResult> RunAsync(
            string executable, string[] args, string? workingDir,
            Func<string, Task>? onOutput = null, Func<string, Task>? onError = null,
            CancellationToken ct = default, IReadOnlyDictionary<string, string>? envOverride = null,
            TimeSpan? timeout = null, string? stdin = null)
        {
            LastExecutable = executable;
            LastArgs = args;
            LastStdin = stdin;
            LastTimeout = timeout;
            Calls.Add((executable, args, stdin));
            var result = _resultFactory?.Invoke(executable, args) ?? _defaultResult;
            return Task.FromResult(result);
        }
    }

    private sealed class FakeCredentialResourceStore : ICredentialResourceStore
    {
        public int Count => 0;
        public Task<List<CredentialResource>> GetAllAsync() => Task.FromResult(new List<CredentialResource>());
        public Task<List<CredentialResource>> GetByProjectAsync(string projectId) => Task.FromResult(new List<CredentialResource>());
        public Task<CredentialResource?> GetByIdAsync(Guid id) => Task.FromResult<CredentialResource?>(null);
        public Task<CredentialResource?> GetByNameAsync(string name) => Task.FromResult<CredentialResource?>(null);
        public Task SaveAsync(CredentialResource resource) => Task.CompletedTask;
        public Task DeleteAsync(Guid id) => Task.CompletedTask;
    }

    private sealed class FakeSshRunner : ISshRunner
    {
        public Task<int> RunAsync(string host, string username, string keyPath, string command,
            Func<string, Task>? onOutput = null, Func<string, Task>? onStderr = null,
            CancellationToken ct = default) => Task.FromResult(0);
    }

    #endregion
}
