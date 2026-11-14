using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Builds;
using ShipRight.Modules.Projects;
using ShipRight.Modules.Resources;
using ShipRight.Shared.Events;
using ShipRight.Shared.ProcessRunner;
using ShipRight.Shared.SshRunner;

namespace ShipRight.Tests.Modules.Builds;

/// <summary>
/// Characterisation tests (Phase 0 of the build-on-server deploy mode). They pin what ShipRight does TODAY when it
/// deploys and rolls back a user's project, so adding a new deploy mode can be proven to change nothing for the
/// existing modes: the exact SSH command per mode, the status transitions, the failure messages, rollback rules and
/// the persisted names of <see cref="DeployMode"/>. These must pass before, and after, any new-mode work.
/// </summary>
[TestClass]
public class DeployBackwardCompatibilityTests
{
    // ── harness ──────────────────────────────────────────────────────────────

    private sealed class CapturingSsh : ISshRunner
    {
        public List<string> Commands { get; } = [];
        public List<(string Host, string User, string Key)> Targets { get; } = [];
        public int ExitCode { get; set; }
        public Exception? Throw { get; set; }

        public Task<int> RunAsync(string host, string username, string keyPath, string command,
            Func<string, Task>? onOutput = null, Func<string, Task>? onStderr = null, CancellationToken ct = default)
        {
            Commands.Add(command);
            Targets.Add((host, username, keyPath));
            if (Throw is not null) throw Throw;
            return Task.FromResult(ExitCode);
        }
    }

    private sealed class MemoryBuildStore : IBuildStore
    {
        private readonly Dictionary<string, BuildRecord> _records = new();
        public int Count => _records.Count;
        public IEnumerable<BuildRecord> All => _records.Values;
        public Task SaveAsync(BuildRecord record) { _records[record.Id] = record; return Task.CompletedTask; }
        public Task<BuildRecord?> GetByIdAsync(string id) => Task.FromResult(_records.TryGetValue(id, out var r) ? r : null);
        public Task<List<BuildRecord>> QueryAsync(string? projectId, string? status, DateTime? from, DateTime? to, string? gitTag, int page, int pageSize) =>
            Task.FromResult(new List<BuildRecord>());
        public Task<int> CountQueryAsync(string? projectId, string? status, DateTime? from, DateTime? to, string? gitTag) => Task.FromResult(0);
        public Task MarkInterruptedAsync() => Task.CompletedTask;
    }

    private sealed class MemoryProjectStore : IProjectStore
    {
        private readonly Dictionary<string, ProjectConfig> _projects = new();
        public int Count => _projects.Count;
        public Task<List<ProjectConfig>> GetAllAsync() => Task.FromResult(_projects.Values.ToList());
        public Task<ProjectConfig?> GetByIdAsync(string id) => Task.FromResult(_projects.TryGetValue(id, out var p) ? p : null);
        public Task<ProjectConfig?> GetByNameAsync(string name) => Task.FromResult(_projects.Values.FirstOrDefault(p => p.Name == name));
        public Task SaveAsync(ProjectConfig project) { _projects[project.Id] = project; return Task.CompletedTask; }
        public Task DeleteAsync(string id) { _projects.Remove(id); return Task.CompletedTask; }
    }

    private sealed class Rig
    {
        public MemoryBuildStore Builds { get; } = new();
        public MemoryProjectStore Projects { get; } = new();
        public CapturingSsh Ssh { get; } = new();
        public BuildEventBus Bus { get; } = new();
        public BuildOrchestrator Orchestrator { get; }

        public Rig()
        {
            // Stores that deploy never touches are null, exactly as FailureCleaningBuildOrchestratorTests does.
            var resolution = new ResourceResolutionService(null!, null!, processRunner: new NoopRunner());
            Orchestrator = new BuildOrchestrator(Builds, Projects, Bus, new NoopRunner(), Ssh, resolution,
                null!, null!, null!, null!);
        }

        public async Task<BuildRecord> SeedAsync(ProjectConfig project, params (string Service, string Image, string Version)[] versions)
        {
            await Projects.SaveAsync(project);
            var record = new BuildRecord
            {
                Id = "b-" + Guid.NewGuid().ToString("N")[..6],
                ProjectId = project.Id,
                ProjectName = project.Name,
                GitTag = "v1",
                Status = BuildStatus.PushSucceeded,
                Versions = versions.Select(v => new ServiceVersion { ServiceName = v.Service, DockerImageName = v.Image, NewVersion = v.Version }).ToList(),
            };
            await Builds.SaveAsync(record);
            return record;
        }
    }

    private sealed class NoopRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string executable, string[] args, string? workingDir,
            Func<string, Task>? onOutput = null, Func<string, Task>? onError = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? envOverride = null, TimeSpan? timeout = null, string? stdin = null) =>
            Task.FromResult(new ProcessResult(0, "", "", TimeSpan.Zero));
    }

    private static ProjectConfig Project(DeployMode mode, string branch = "master", params string[] composeNames)
    {
        var names = composeNames.Length > 0 ? composeNames : [string.Empty];
        return new ProjectConfig
        {
            Id = "p1", Name = "Shop",
            Services = names.Select((n, i) => new ServiceConfig
            {
                Name = i == 0 ? "api" : $"svc{i}", DockerImageName = i == 0 ? "owner/api" : $"owner/svc{i}", ComposeServiceName = n,
            }).ToList(),
            GitRepos = [new GitConfig { RepoPath = "/src", DeployBranch = branch }],
            Server = new ServerConfig
            {
                Host = "10.0.0.5", Username = "deploy", SshKeyPath = "/keys/k", RemoteWorkingDir = "/app",
                RebuildScript = "rebuild.sh", DeployMode = mode,
            },
        };
    }

    // ── the persisted shape of DeployMode ────────────────────────────────────

    [TestMethod]
    public void DeployMode_ExistingValuesKeepTheirNamesAndOrdinals()
    {
        // Stored projects hold these (as names via JsonStringEnumConverter, possibly as ints). A new mode must be
        // appended; none of these may be renamed or renumbered.
        Assert.AreEqual(0, (int)DeployMode.GitScript);
        Assert.AreEqual(1, (int)DeployMode.GitCompose);
        Assert.AreEqual(2, (int)DeployMode.EnvCompose);
        CollectionAssert.AreEqual(new[] { "GitScript", "GitCompose", "EnvCompose" },
            Enum.GetNames<DeployMode>().Take(3).ToArray());
    }

    [TestMethod]
    public void DeployMode_ExistingValuesSerialiseToTheirNames_AndReadBackFromNamesAndInts()
    {
        var opts = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        Assert.AreEqual("\"GitScript\"", JsonSerializer.Serialize(DeployMode.GitScript, opts));
        Assert.AreEqual("\"GitCompose\"", JsonSerializer.Serialize(DeployMode.GitCompose, opts));
        Assert.AreEqual("\"EnvCompose\"", JsonSerializer.Serialize(DeployMode.EnvCompose, opts));
        Assert.AreEqual(DeployMode.EnvCompose, JsonSerializer.Deserialize<DeployMode>("\"EnvCompose\"", opts));
        Assert.AreEqual(DeployMode.GitCompose, JsonSerializer.Deserialize<DeployMode>("1", opts));
    }

    [TestMethod]
    public void ServerConfig_DefaultsToGitScript_SoOldRecordsKeepTheirBehaviour()
    {
        Assert.AreEqual(DeployMode.GitScript, new ServerConfig().DeployMode);
        Assert.AreEqual("rebuild.sh", new ServerConfig().RebuildScript);
    }

    // ── the exact deploy command per mode ────────────────────────────────────

    [TestMethod]
    public async Task GitScript_PullsTheDeployBranchThenRunsTheRebuildScript()
    {
        var rig = new Rig();
        var rec = await rig.SeedAsync(Project(DeployMode.GitScript), ("api", "owner/api", "1.2.3"));

        await rig.Orchestrator.DeployAsync(rec.Id);

        Assert.AreEqual("cd /app && git pull origin master && bash rebuild.sh", rig.Ssh.Commands.Single());
        Assert.AreEqual(("10.0.0.5", "deploy", "/keys/k"), rig.Ssh.Targets.Single());
    }

    [TestMethod]
    public async Task GitScript_UsesTheConfiguredDeployBranch()
    {
        var rig = new Rig();
        var rec = await rig.SeedAsync(Project(DeployMode.GitScript, branch: "release"), ("api", "owner/api", "1.2.3"));

        await rig.Orchestrator.DeployAsync(rec.Id);

        Assert.AreEqual("cd /app && git pull origin release && bash rebuild.sh", rig.Ssh.Commands.Single());
    }

    [TestMethod]
    public async Task GitCompose_WhenEveryServiceHasAComposeName_PullsAndRestartsOnlyThose()
    {
        var rig = new Rig();
        var rec = await rig.SeedAsync(Project(DeployMode.GitCompose, "master", "api", "web"), ("api", "owner/api", "1.2.3"));

        await rig.Orchestrator.DeployAsync(rec.Id);

        Assert.AreEqual(
            "cd /app && git pull origin master && docker compose pull api web && docker compose up -d --no-deps api web",
            rig.Ssh.Commands.Single());
    }

    [TestMethod]
    public async Task GitCompose_WhenAServiceHasNoComposeName_RestartsTheWholeStack()
    {
        var rig = new Rig();
        var rec = await rig.SeedAsync(Project(DeployMode.GitCompose, "master", "api", ""), ("api", "owner/api", "1.2.3"));

        await rig.Orchestrator.DeployAsync(rec.Id);

        Assert.AreEqual("cd /app && git pull origin master && docker compose pull && docker compose up -d",
            rig.Ssh.Commands.Single());
    }

    [TestMethod]
    public async Task EnvCompose_ExportsEachBuiltServicesTagVariableBeforeComposeUp()
    {
        var rig = new Rig();
        var rec = await rig.SeedAsync(Project(DeployMode.EnvCompose, "master", "api"), ("api", "owner/api", "1.2.3"));

        await rig.Orchestrator.DeployAsync(rec.Id);

        Assert.AreEqual(
            "cd /app && export API_TAG=owner/api:1.2.3 && docker compose pull api && docker compose up -d --no-deps api",
            rig.Ssh.Commands.Single());
    }

    [TestMethod]
    public async Task EnvCompose_TagVariableNameIsUpperCasedWithNonAlphanumericsAsUnderscores()
    {
        var rig = new Rig();
        var rec = await rig.SeedAsync(Project(DeployMode.EnvCompose), ("my-api.v2", "owner/my-api", "2.0.0"), ("web", "owner/web", "3.1.0"));

        await rig.Orchestrator.DeployAsync(rec.Id);

        StringAssert.Contains(rig.Ssh.Commands.Single(), "export MY_API_V2_TAG=owner/my-api:2.0.0 WEB_TAG=owner/web:3.1.0 &&");
    }

    [TestMethod]
    public async Task ADeployModeOverride_WinsOverTheProjectsMode()
    {
        var rig = new Rig();
        var rec = await rig.SeedAsync(Project(DeployMode.GitScript, "master", "api"), ("api", "owner/api", "1.2.3"));

        await rig.Orchestrator.DeployAsync(rec.Id, DeployMode.EnvCompose);

        StringAssert.StartsWith(rig.Ssh.Commands.Single(), "cd /app && export API_TAG=owner/api:1.2.3");
    }

    // ── outcomes ─────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task ASuccessfulDeploy_MarksTheBuildDeployed_AndStampsTheTime()
    {
        var rig = new Rig();
        var rec = await rig.SeedAsync(Project(DeployMode.GitScript), ("api", "owner/api", "1.2.3"));

        await rig.Orchestrator.DeployAsync(rec.Id);

        var saved = (await rig.Builds.GetByIdAsync(rec.Id))!;
        Assert.AreEqual(BuildStatus.Deployed, saved.Status);
        Assert.IsNotNull(saved.DeployedAt);
        Assert.IsTrue(saved.StepDurations.ContainsKey("Deploy"));
    }

    [TestMethod]
    public async Task ANonZeroExit_MarksDeployFailed_WithTheExitCodeInTheSummary()
    {
        var rig = new Rig();
        rig.Ssh.ExitCode = 3;
        var rec = await rig.SeedAsync(Project(DeployMode.GitScript), ("api", "owner/api", "1.2.3"));

        await rig.Orchestrator.DeployAsync(rec.Id);

        var saved = (await rig.Builds.GetByIdAsync(rec.Id))!;
        Assert.AreEqual(BuildStatus.DeployFailed, saved.Status);
        Assert.AreEqual("Deploy exited with code 3. Check server state manually.", saved.ErrorSummary);
        Assert.IsNull(saved.DeployedAt);
    }

    [TestMethod]
    public async Task ALostSshConnection_MarksDeployFailed_AndNamesTheHost()
    {
        var rig = new Rig();
        rig.Ssh.Throw = new IOException("connection reset");
        var rec = await rig.SeedAsync(Project(DeployMode.GitScript), ("api", "owner/api", "1.2.3"));

        await rig.Orchestrator.DeployAsync(rec.Id);

        var saved = (await rig.Builds.GetByIdAsync(rec.Id))!;
        Assert.AreEqual(BuildStatus.DeployFailed, saved.Status);
        StringAssert.Contains(saved.ErrorSummary, "SSH connection lost: connection reset");
        StringAssert.Contains(saved.ErrorSummary, "10.0.0.5");
    }

    [TestMethod]
    public async Task DeployingAnUnknownBuildOrProject_DoesNothing()
    {
        var rig = new Rig();
        await rig.Orchestrator.DeployAsync("nope");
        Assert.AreEqual(0, rig.Ssh.Commands.Count);

        var orphan = new BuildRecord { Id = "orphan", ProjectId = "missing" };
        await rig.Builds.SaveAsync(orphan);
        await rig.Orchestrator.DeployAsync("orphan");
        Assert.AreEqual(0, rig.Ssh.Commands.Count);
        Assert.AreEqual(BuildStatus.Pending, (await rig.Builds.GetByIdAsync("orphan"))!.Status);
    }

    // ── rollback ─────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Rollback_IsRefusedForGitScriptProjects()
    {
        var rig = new Rig();
        var rec = await rig.SeedAsync(Project(DeployMode.GitScript), ("api", "owner/api", "1.0.0"));

        var ex = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => rig.Orchestrator.RollbackAsync(rec.Id));

        Assert.AreEqual("Rollback requires GitCompose or EnvCompose deploy mode.", ex.Message);
        Assert.AreEqual(0, rig.Ssh.Commands.Count);
    }

    [TestMethod]
    public async Task Rollback_OfAnUnknownBuild_Throws()
    {
        var rig = new Rig();
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => rig.Orchestrator.RollbackAsync("nope"));
    }

    [TestMethod]
    public async Task Rollback_EnvCompose_RedeploysTheTargetsVersionsAsANewRollbackRecord()
    {
        var rig = new Rig();
        var target = await rig.SeedAsync(Project(DeployMode.EnvCompose, "master", "api"), ("api", "owner/api", "1.0.0"));

        var newId = await rig.Orchestrator.RollbackAsync(target.Id);
        var saved = await WaitForOutcomeAsync(rig.Builds, newId);

        Assert.AreEqual(BuildStatus.Deployed, saved.Status);
        Assert.IsTrue(saved.IsRollback);
        Assert.AreEqual(target.Id, saved.RolledBackFromBuildId);
        Assert.AreEqual(target.GitTag, saved.GitTag);
        Assert.AreEqual(
            "cd /app && export API_TAG=owner/api:1.0.0 && docker compose pull api && docker compose up -d --no-deps api",
            rig.Ssh.Commands.Single());
    }

    [TestMethod]
    public async Task Rollback_EnvCompose_WhenTheServerCommandFails_MarksTheRollbackDeployFailed()
    {
        var rig = new Rig();
        rig.Ssh.ExitCode = 7;
        var target = await rig.SeedAsync(Project(DeployMode.EnvCompose, "master", "api"), ("api", "owner/api", "1.0.0"));

        var newId = await rig.Orchestrator.RollbackAsync(target.Id);
        var saved = await WaitForOutcomeAsync(rig.Builds, newId);

        Assert.AreEqual(BuildStatus.DeployFailed, saved.Status);
        Assert.AreEqual("Rollback command exited with code 7.", saved.ErrorSummary);
    }

    private static async Task<BuildRecord> WaitForOutcomeAsync(MemoryBuildStore store, string id)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var r = await store.GetByIdAsync(id);
            if (r is not null && r.Status is BuildStatus.Deployed or BuildStatus.DeployFailed) return r;
            await Task.Delay(25);
        }
        Assert.Fail("The rollback did not finish in time.");
        return null!;
    }
}
