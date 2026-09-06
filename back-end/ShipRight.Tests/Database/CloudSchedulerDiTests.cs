using Jattac.Libs.Tempo.Scheduling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Database;
using ShipRight.Modules.Database.Providers;
using ShipRight.Modules.Projects;
using ShipRight.Modules.Scheduler;
using ShipRight.Shared.Events;
using ShipRight.Shared.SshRunner;
using ShipRight.Shared.Store;

namespace ShipRight.Tests.Database;

[TestClass]
public class CloudSchedulerDiTests
{
    private sealed class StubProjectStore : IProjectStore
    {
        public int Count => 0;

        public Task<List<ProjectConfig>> GetAllAsync() => Task.FromResult(new List<ProjectConfig>());

        public Task<ProjectConfig?> GetByIdAsync(string id) => Task.FromResult<ProjectConfig?>(null);

        public Task<ProjectConfig?> GetByNameAsync(string name) => Task.FromResult<ProjectConfig?>(null);

        public Task SaveAsync(ProjectConfig project) => Task.CompletedTask;

        public Task DeleteAsync(string id) => Task.CompletedTask;
    }

    private sealed class StubDbProviderResolver : IDbProviderResolver
    {
        public IDbProvider Resolve(DbProviderType providerType) => null!;
    }

    private sealed class StubSshRunner : ISshRunner
    {
        public Task<int> RunAsync(string host, string username, string keyPath,
            string command, Func<string, Task>? onOutput = null,
            Func<string, Task>? onStderr = null, CancellationToken ct = default) =>
            Task.FromResult(0);
    }

    private static ServiceProvider BuildCloudProvider()
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddSingleton<IProjectStore>(new StubProjectStore());
        services.AddSingleton<DatabaseOrchestrator>(_ => new DatabaseOrchestrator(
            new StubDbProviderResolver(), new StubSshRunner(), new KnownHostsStore(), new BuildEventBus()));
        services.AddSingleton(_ => new MariaDbBackupHistoryStore(null!));
        CloudSchedulerRegistrar.Register(services);

        return services.BuildServiceProvider();
    }

    [TestMethod]
    public async Task CloudSchedulerGraph_Resolves_WithoutCircularDependency()
    {
        await using var provider = BuildCloudProvider();

        var scheduler = provider.GetRequiredService<TempoScheduler<BackupJob>>();
        Assert.IsNotNull(scheduler);

        var overflow = provider.GetRequiredService<BackupOverflowStore>();
        Assert.IsNotNull(overflow);
    }
}
