using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Database;
using ShipRight.Modules.Database.Providers;
using ShipRight.Modules.Projects;
using ShipRight.Modules.Scheduler;
using ShipRight.Shared.Events;
using ShipRight.Shared.SshRunner;
using Jattac.Libs.Tempo;
using Jattac.Libs.Tempo.Scheduling;

namespace ShipRight.Tests.Modules.Scheduler;

[TestClass]
public class BackupJobProcessorTests
{
    [TestMethod]
    public async Task ProcessAsync_ProjectFound_CallsOrchestratorAndReturnsTrue()
    {
        var orchestrator = new FakeOrchestrator(result: true);
        var projectStore = new FakeProjectStore(ProjectWithDb("proj-1"));
        var processor = new BackupJobProcessor(projectStore, orchestrator);

        var result = await processor.ProcessAsync(WorkFor("proj-1"), default);

        Assert.IsTrue(result);
        Assert.AreEqual("proj-1", orchestrator.LastProject?.Id);
    }

    [TestMethod]
    public async Task ProcessAsync_OrchestratorReturnsFalse_PropagatesResult()
    {
        var orchestrator = new FakeOrchestrator(result: false);
        var projectStore = new FakeProjectStore(ProjectWithDb("proj-1"));
        var processor = new BackupJobProcessor(projectStore, orchestrator);

        var result = await processor.ProcessAsync(WorkFor("proj-1"), default);

        Assert.IsFalse(result);
    }

    [TestMethod]
    public async Task ProcessAsync_ProjectNotFound_ReturnsFalse()
    {
        var orchestrator = new FakeOrchestrator(result: true);
        var projectStore = new FakeProjectStore(null);
        var processor = new BackupJobProcessor(projectStore, orchestrator);

        var result = await processor.ProcessAsync(WorkFor("missing"), default);

        Assert.IsFalse(result);
        Assert.IsNull(orchestrator.LastProject);
    }

    [TestMethod]
    public async Task ProcessAsync_ProjectHasNoDatabase_ReturnsFalse()
    {
        var orchestrator = new FakeOrchestrator(result: true);
        var projectStore = new FakeProjectStore(new ProjectConfig
        {
            Id = "no-db",
            Name = "No DB",
            Database = null,
        });
        var processor = new BackupJobProcessor(projectStore, orchestrator);

        var result = await processor.ProcessAsync(WorkFor("no-db"), default);

        Assert.IsFalse(result);
        Assert.IsNull(orchestrator.LastProject);
    }

    [TestMethod]
    public async Task ProcessAsync_ProjectHasEmptyDatabaseName_ReturnsFalse()
    {
        var orchestrator = new FakeOrchestrator(result: true);
        var projectStore = new FakeProjectStore(new ProjectConfig
        {
            Id = "empty-db",
            Name = "Empty DB",
            Database = new DatabaseConfig { DatabaseName = "" },
        });
        var processor = new BackupJobProcessor(projectStore, orchestrator);

        var result = await processor.ProcessAsync(WorkFor("empty-db"), default);

        Assert.IsFalse(result);
        Assert.IsNull(orchestrator.LastProject);
    }

    private static TempoScheduledWork<BackupJob> WorkFor(string projectId) => new(
        Job: new BackupJob
        {
            TenantId = BackupJob.TenantIdFromProject(projectId),
            ProjectId = projectId,
            ProjectName = $"Project-{projectId}",
            DatabaseName = "test-db",
        },
        ScheduleId: Guid.NewGuid(),
        ScheduledAt: DateTimeOffset.UtcNow,
        EnqueuedAt: DateTimeOffset.UtcNow);

    private static ProjectConfig ProjectWithDb(string id) => new()
    {
        Id = id,
        Name = $"Project-{id}",
        Database = new DatabaseConfig
        {
            Provider = DbProviderType.MariaDb,
            DatabaseName = "test-db",
            ContainerName = "db",
        },
        Server = new ServerConfig { Host = "localhost", Username = "test" },
        CreatedAt = DateTime.UtcNow,
    };

    private sealed class FakeProjectStore : IProjectStore
    {
        private readonly ProjectConfig? _project;
        public int Count => _project is null ? 0 : 1;

        public FakeProjectStore(ProjectConfig? project) => _project = project;

        public Task<List<ProjectConfig>> GetAllAsync() =>
            Task.FromResult(_project is null ? new List<ProjectConfig>() : new List<ProjectConfig> { _project });
        public Task<ProjectConfig?> GetByIdAsync(string id) =>
            Task.FromResult<ProjectConfig?>(_project?.Id == id ? _project : null);
        public Task<ProjectConfig?> GetByNameAsync(string name) =>
            Task.FromResult<ProjectConfig?>(_project?.Name == name ? _project : null);
        public Task SaveAsync(ProjectConfig project) => Task.CompletedTask;
        public Task DeleteAsync(string id) => Task.CompletedTask;
    }

    private sealed class FakeOrchestrator : DatabaseOrchestrator
    {
        private readonly bool _result;
        public ProjectConfig? LastProject { get; private set; }

        public FakeOrchestrator(bool result)
            : base(new FakeResolver(), new FakeSshRunner(), new KnownHostsStore(), new BuildEventBus())
        {
            _result = result;
        }

        public override Task<bool> ScheduledBackupAsync(ProjectConfig project, CancellationToken ct)
        {
            LastProject = project;
            return Task.FromResult(_result);
        }
    }

    private sealed class FakeResolver : IDbProviderResolver
    {
        public IDbProvider Resolve(DbProviderType providerType) => null!;
    }

    private sealed class FakeSshRunner : ISshRunner
    {
        public Task<int> RunAsync(string host, string username, string keyPath,
            string command, Func<string, Task>? onOutput = null,
            Func<string, Task>? onStderr = null, CancellationToken ct = default) =>
            Task.FromResult(0);
    }
}
