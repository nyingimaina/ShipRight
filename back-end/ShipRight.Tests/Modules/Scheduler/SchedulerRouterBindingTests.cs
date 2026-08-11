using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Database;
using ShipRight.Modules.Database.Providers;
using ShipRight.Modules.Projects;
using ShipRight.Modules.Scheduler;
using ShipRight.Shared.Events;
using ShipRight.Shared.SshRunner;
using ShipRight.Shared.Store;

namespace ShipRight.Tests.Modules.Scheduler;

[TestClass]
public class SchedulerRouterBindingTests
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

    private static WebApplication BuildCloudApp()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IProjectStore>(new StubProjectStore());
        builder.Services.AddSingleton<DatabaseOrchestrator>(_ => new DatabaseOrchestrator(
            new StubDbProviderResolver(), new StubSshRunner(), new KnownHostsStore(), new BuildEventBus()));
        builder.Services.AddSingleton(_ => new MariaDbBackupHistoryStore(null!));
        CloudSchedulerRegistrar.Register(builder.Services);
        return builder.Build();
    }

    [TestMethod]
    public void CloudHistoryRoute_BindsRegisteredStore_WithoutEndpointFailure()
    {
        var provider = BuildCloudApp().Services;
        var options = new RequestDelegateFactoryOptions
        {
            ServiceProvider = provider,
            DisableInferBodyFromParameters = true,
        };

        var factory = RequestDelegateFactory.Create(
            (MariaDbBackupHistoryStore store, DateTime? since = null, string? status = null) =>
                Results.Ok(new { records = new object[0] }), options);

        Assert.IsNotNull(factory);
    }

    [TestMethod]
    public void CloudMode_UnregisteredStoreParameter_ThrowsAtMetadataCreation()
    {
        var provider = BuildCloudApp().Services;
        var options = new RequestDelegateFactoryOptions
        {
            ServiceProvider = provider,
            DisableInferBodyFromParameters = true,
        };

        Assert.ThrowsException<InvalidOperationException>(() =>
            RequestDelegateFactory.Create(
                (BackupHistoryStore store) => Results.Ok(new { ok = true }), options));
    }
}
