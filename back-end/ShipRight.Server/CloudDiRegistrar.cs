using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Jattac.Libs.Tempo;
using Jattac.Libs.Tempo.Scheduling;
using Serilog;
using ShipRight.Database;
using ShipRight.Modules.Auth.Services;
using ShipRight.Modules.Builds;
using ShipRight.Modules.Database;
using ShipRight.Modules.Database.Providers;
using ShipRight.Modules.Projects;
using ShipRight.Modules.RemoteHost;
using ShipRight.Modules.Resources;
using ShipRight.Modules.Resources.Stores;
using ShipRight.Modules.Scheduler;
using ShipRight.Modules.Servers;
using ShipRight.Modules.Services;
using ShipRight.Modules.Ssh;
using ShipRight.Modules.WatchBranch;
using ShipRight.Shared.Events;
using ShipRight.Shared.ProcessRunner;
using ShipRight.Shared.SshRunner;
using ShipRight.Shared.Store;

namespace ShipRight.Server;

public static class CloudDiRegistrar
{
    public static void Register(IServiceCollection services, ConfigurationManager config)
    {
        CloudDatabaseRegistrar.Register(
            services,
            config,
            Environment.GetEnvironmentVariable("SHIPRIGHT__DB_CONNECTION"));

        services.AddSingleton<IPasswordHelper, PasswordHelper>();

        var jwtKey = Environment.GetEnvironmentVariable("SHIPRIGHT__JWT_KEY")
            ?? config["Jwt:SigningKey"]
            ?? throw new InvalidOperationException("JWT signing key is required. Set SHIPRIGHT__JWT_KEY env var.");
        services.AddSingleton<IJwtService>(_ => new JwtService(jwtKey));
        services.AddSingleton<ITokenService, TokenService>();
        services.AddSingleton<ISetupService, SetupService>();

        services.AddRateLimiter(options =>
        {
            options.AddFixedWindowLimiter("Api", opt =>
            {
                opt.PermitLimit = 100;
                opt.Window = TimeSpan.FromMinutes(1);
                opt.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
                opt.QueueLimit = 0;
            });
            options.RejectionStatusCode = 429;
        });

        // MariaDB-backed stores
        services.AddSingleton<IProjectStore, MariaDbProjectStore>();
        services.AddSingleton<IServerStore, MariaDbServerStore>();
        services.AddSingleton<IBuildStore, MariaDbBuildStore>();
        services.AddSingleton<IDockerRegistryResourceStore, MariaDbDockerRegistryResourceStore>();
        services.AddSingleton<IScriptResourceStore, MariaDbScriptResourceStore>();
        services.AddSingleton<ICredentialResourceStore, MariaDbCredentialResourceStore>();
        services.AddSingleton<IPipelineResourceStore, MariaDbPipelineResourceStore>();
        services.AddSingleton<MariaDbBackupHistoryStore>();
        services.AddSingleton<MariaDbWatchBranchHistoryStore>();

        // Shared infrastructure
        services.AddSingleton<BuildEventBus>();
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<KnownHostsStore>();
        services.AddSingleton<ISshRunner, SshRunner>();
        services.AddSingleton<BuildOrchestrator>();
        services.AddSingleton<MariaDbProvider>();
        services.AddSingleton<SqlServerProvider>();
        services.AddSingleton<IDbProviderResolver>(sp => new DbProviderResolver(
            sp.GetRequiredService<MariaDbProvider>(),
            sp.GetRequiredService<SqlServerProvider>()));
        services.AddSingleton<DatabaseOrchestrator>();
        services.AddSingleton<IRemoteHostProvider, LinuxSshProvider>();
        services.AddSingleton<IMonitoringProvider, LinuxSshMonitoringProvider>();
        services.AddSingleton<SshKeyStore>(_ => new SshKeyStore(DataDirectory.Resolve()));
        services.AddSingleton<ResourceResolutionService>();
        services.AddSingleton<ScriptExecutor>();

        // ── Scheduler (cloud: MariaDB-backed history) ──────────────────────────
        CloudSchedulerRegistrar.Register(services);

        // ── Watch Branch (cloud: MariaDB-backed history) ──────────────────────
        services.AddSingleton<TempoQueue<TempoScheduledWork<WatchBranchJob>>>(sp =>
        {
            var processor = new WatchBranchJobProcessor(
                sp.GetRequiredService<IProjectStore>(),
                sp.GetRequiredService<BuildOrchestrator>(),
                sp.GetRequiredService<IProcessRunner>());
            var queue = new TempoQueue<TempoScheduledWork<WatchBranchJob>>(processor,
                new TempoQueueSettings
                {
                    MessagesPerSecond = 1, BurstCapacity = 3, ChannelCapacity = 200,
                    MaxPendingItemsPerTenant = 3, ResumeThresholdFactor = 0.5,
                    PriorityLaneCount = 1, DepthReportInterval = TimeSpan.FromSeconds(60),
                    ObservabilityReportInterval = TimeSpan.FromSeconds(60),
                }, sp.GetService<ILoggerFactory>());
            var history = sp.GetRequiredService<MariaDbWatchBranchHistoryStore>();

            queue.OnProcessed += async (correlationId, work) =>
            {
                await history.Append(new WatchBranchHistoryRecord
                {
                    ProjectId = work.Job.ProjectId, ProjectName = work.Job.ProjectName,
                    BranchName = work.Job.BranchName, Status = "triggered",
                    TriggeredAt = DateTime.UtcNow, ScheduleId = work.ScheduleId,
                    CorrelationId = correlationId,
                });
            };

            queue.OnFailed += async (correlationId, work, error) =>
            {
                await history.Append(new WatchBranchHistoryRecord
                {
                    ProjectId = work.Job.ProjectId, ProjectName = work.Job.ProjectName,
                    BranchName = work.Job.BranchName, Status = "failed",
                    TriggeredAt = DateTime.UtcNow, ErrorMessage = error,
                    ScheduleId = work.ScheduleId, CorrelationId = correlationId,
                });
            };

            queue.OnDepthReport += snapshot =>
            {
                Log.Information("WatchBranch queue depth: pending={Pending}", snapshot.PendingCount);
                return Task.CompletedTask;
            };

            return queue;
        });

        services.AddSingleton<TempoScheduler<WatchBranchJob>>(sp =>
        {
            var scheduler = new TempoScheduler<WatchBranchJob>(
                sp.GetRequiredService<TempoQueue<TempoScheduledWork<WatchBranchJob>>>(),
                new TempoSchedulerSettings { TickInterval = TimeSpan.FromSeconds(10), MaxCatchUpSlots = 10 });
            var projects = sp.GetRequiredService<IProjectStore>().GetAllAsync().GetAwaiter().GetResult();
            var count = 0;
            foreach (var p in projects)
            {
                if (string.IsNullOrWhiteSpace(p.WatchBranch) || p.GitRepos.Count == 0) continue;
                var interval = TimeSpan.FromSeconds(Math.Max(60, p.WatchPollSeconds));
                try
                {
                    scheduler.Register(new WatchBranchJob
                    {
                        TenantId = WatchBranchJob.TenantIdFromProject(p.Id), ProjectId = p.Id,
                        ProjectName = p.Name, RepoPath = p.GitRepos[0].RepoPath,
                        BranchName = p.WatchBranch, WatchSteps = p.WatchSteps,
                    }, new TempoSchedule.Every(interval), MissedRunPolicy.Skip, OverlapPolicy.Skip);
                    count++;
                }
                catch (Exception ex) { Log.Error(ex, "WatchBranch: failed to register schedule for {Project}", p.Name); }
            }
            Log.Information("WatchBranch: {Count} watch schedules registered at startup", count);
            return scheduler;
        });

        services.AddHostedService<WatchBranchHostedService>();
    }
}
