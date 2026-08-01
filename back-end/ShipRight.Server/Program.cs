using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Reflection;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Dapper.Contrib.Extensions;
using Jattac.Libs.Tempo;
using Jattac.Libs.Tempo.Scheduling;
using Jattac.Libraries.QBuilder.Config;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Logging;
using Rocket.Libraries.DatabaseIntegrator;
using Serilog;
using Serilog.Formatting.Json;
using ShipRight.Database;
using ShipRight.Database.Models;
using ShipRight.Modules.Auth;
using ShipRight.Modules.Auth.Middleware;
using ShipRight.Modules.Auth.Models;
using ShipRight.Modules.Auth.Services;
using ShipRight.Modules.Builds;
using ShipRight.Modules.Database;
using ShipRight.Modules.Database.Providers;
using ShipRight.Modules.Filesystem;
using ShipRight.Modules.Projects;
using ShipRight.Modules.RepoMaintenance;
using ShipRight.Modules.RemoteHost;
using ShipRight.Modules.Resources;
using ShipRight.Modules.Resources.Models;
using ShipRight.Modules.Resources.Stores;
using ShipRight.Modules.Scheduler;
using ShipRight.Modules.WatchBranch;
using ShipRight.Modules.Services;
using ShipRight.Modules.Servers;
using ShipRight.Modules.Ssh;
using ShipRight.Modules.System;
using ShipRight.Shared.Events;
using ShipRight.Shared.ProcessRunner;
using ShipRight.Shared.SshRunner;
using ShipRight.Shared.Store;

var dataDir = DataDirectory.Resolve();
var cloudMode = args.Contains("--cloud") || string.Equals(
    Environment.GetEnvironmentVariable("SHIPRIGHT__MODE"), "cloud", StringComparison.OrdinalIgnoreCase);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.Hosting.Lifetime", Serilog.Events.LogEventLevel.Information)
    .MinimumLevel.Override("System", Serilog.Events.LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(
        new JsonFormatter(),
        Path.Combine(dataDir, "logs", "shipright-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30)
    .CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    if (cloudMode)
    {
        builder.WebHost.UseUrls("http://0.0.0.0:5200");
        Log.Information("Starting in CLOUD mode");
    }
    else
    {
        builder.WebHost.UseUrls("http://127.0.0.1:5200");
        Log.Information("Starting in DESKTOP mode");
    }

    builder.Host.UseSerilog();

    builder.Services.ConfigureHttpJsonOptions(o =>
        o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

    var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
        ?? (cloudMode
            ? [Environment.GetEnvironmentVariable("SHIPRIGHT__CORS_ORIGINS") ?? "http://localhost:5200"]
            : ["http://localhost:5200", "http://127.0.0.1:5200"]);

    builder.Services.AddCors(options =>
        options.AddPolicy("ShipRightPolicy", policy =>
        {
            if (cloudMode)
            {
                policy.WithOrigins(allowedOrigins)
                      .AllowAnyHeader()
                      .AllowAnyMethod()
                      .AllowCredentials();
            }
            else
            {
                policy.WithOrigins(allowedOrigins)
                      .AllowAnyHeader()
                      .AllowAnyMethod();
            }
        }));

    // ─── DI Registration ───────────────────────────────────────────

    if (cloudMode)
    {
        RegisterCloudServices(builder.Services, builder.Configuration);
    }
    else
    {
        RegisterDesktopServices(builder.Services, dataDir);
    }

    var app = builder.Build();

    // ─── Middleware Pipeline ────────────────────────────────────────

    app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
    {
        context.Response.StatusCode = 500;
        context.Response.ContentType = "application/json";
        var requestId = context.TraceIdentifier;
        Log.Fatal("Unhandled exception on {Path} [{RequestId}]", context.Request.Path, requestId);
        await context.Response.WriteAsJsonAsync(new
        {
            isError = true,
            message = $"An unexpected error occurred. Check logs for request ID {requestId}."
        });
    }));

    if (cloudMode)
    {
        app.UseRateLimiter();
        app.UseMiddleware<DatabaseTransactionMiddleware>();
        app.UseMiddleware<JwtMiddleware>();
    }

    app.UseCors("ShipRightPolicy");
    app.UseDefaultFiles();
    app.UseStaticFiles();

    // ─── Routes ────────────────────────────────────────────────────

    if (cloudMode)
    {
        app.MapAuthRoutes();
    }

    app.MapHealthRoutes();
    app.MapSystemRoutes();
    app.MapFsRoutes();
    app.MapProjectRoutes();
    app.MapProjectSummaryRoutes();
    app.MapBuildRoutes();
    app.MapDatabaseRoutes();
    app.MapSshTerminalRoutes();
    app.MapContainerLogRoutes();
    app.MapRepoMaintenanceRoutes();
    app.MapServerRoutes();
    app.MapSchedulerRoutes();
    app.MapSshKeyRoutes();
    app.MapServerSshKeyRoutes();
    app.MapWatchBranchRoutes();
    app.MapResourceRoutes();
    app.MapPipelineRoutes();

    app.MapFallbackToFile("index.html");

    // ─── Startup ───────────────────────────────────────────────────

    if (cloudMode)
    {
        var adminEmail = Environment.GetEnvironmentVariable("SHIPRIGHT__ADMIN_EMAIL");
        var adminPassword = Environment.GetEnvironmentVariable("SHIPRIGHT__ADMIN_PASSWORD");
        if (!string.IsNullOrEmpty(adminEmail) && !string.IsNullOrEmpty(adminPassword))
        {
            var setupService = app.Services.GetRequiredService<ISetupService>();
            await setupService.EnsureAdminUserAsync(adminEmail, adminPassword);
        }
    }

    await app.Services.GetRequiredService<IBuildStore>().MarkInterruptedAsync();

    var projectCount = app.Services.GetRequiredService<IProjectStore>().Count;
    var buildCount   = app.Services.GetRequiredService<IBuildStore>().Count;
    Log.Information("ShipRight starting on port {Port}", 5200);
    Log.Information("Data directory: {DataDir}", dataDir);
    Log.Information("{ProjectCount} projects, {BuildCount} builds loaded", projectCount, buildCount);

    await app.StartAsync();

    if (args.Contains("--browser"))
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(1500);
            try
            {
                Process.Start(new ProcessStartInfo("http://127.0.0.1:5200") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to open browser");
            }
        });
    }

    await app.WaitForShutdownAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "ShipRight failed to start");
}
finally
{
    Log.CloseAndFlush();
}

// ─── Service Registration Methods ───────────────────────────────────

static void RegisterCloudServices(IServiceCollection services, ConfigurationManager config)
{
    QBuilderConfig.ConfigureDefault(opt =>
    {
        opt.TableNameResolver = t =>
        {
            var attr = t.GetCustomAttribute<TableAttribute>();
            if (attr?.Name != null) return attr.Name;
            var name = t.Name;
            if (name.EndsWith("Record")) return name[..^6];
            if (name.EndsWith("Dto")) return name[..^3];
            return name;
        };
    });

    config["DatabaseConnectionSettings:ConnectionString"] = Environment.GetEnvironmentVariable("SHIPRIGHT__DB_CONNECTION")
        ?? config.GetConnectionString("DefaultConnection")
        ?? "Server=localhost;Port=3306;Database=shipright;User Id=root;Password=;Charset=utf8;ConvertZeroDateTime=True;";

    services.Configure<DatabaseSettings>(config.GetSection("DatabaseConnectionSettings"));

    services.AddSingleton<IConnectionProvider, DatabaseConnectionProvider>();
    services.AddSingleton<IDatabaseHelper<Guid>, DatabaseHelper<Guid>>();
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
    services.AddSingleton<ResourceResolutionService>();
    services.AddSingleton<ScriptExecutor>();

    // ── Scheduler (cloud: MariaDB-backed history) ──────────────────────────
    services.AddSingleton<BackupOverflowStore>(sp =>
        new BackupOverflowStore(DataDirectory.Resolve(), sp.GetRequiredService<TempoScheduler<BackupJob>>()));

    services.AddSingleton<TempoQueue<TempoScheduledWork<BackupJob>>>(sp =>
    {
        var processor = new BackupJobProcessor(
            sp.GetRequiredService<IProjectStore>(),
            sp.GetRequiredService<DatabaseOrchestrator>());
        var queue = new TempoQueue<TempoScheduledWork<BackupJob>>(processor,
            new TempoQueueSettings
            {
                MessagesPerSecond = 3, BurstCapacity = 6, ChannelCapacity = 500,
                MaxPendingItemsPerTenant = 5, ResumeThresholdFactor = 0.5,
                PriorityLaneCount = 2, DepthReportInterval = TimeSpan.FromSeconds(30),
                ObservabilityReportInterval = TimeSpan.FromSeconds(30),
            }, sp.GetService<ILoggerFactory>());
        var history = sp.GetRequiredService<MariaDbBackupHistoryStore>();
        var overflow = sp.GetRequiredService<BackupOverflowStore>();

        queue.OnProcessed += async (correlationId, work) =>
        {
            var job = work.Job;
            await history.Append(new BackupHistoryRecord
            {
                ProjectId = job.ProjectId, ProjectName = job.ProjectName,
                DatabaseName = job.DatabaseName, Status = "completed",
                CompletedAt = DateTime.UtcNow, DurationMs = 0,
                CorrelationId = correlationId, ScheduleId = work.ScheduleId,
            });
        };

        queue.OnFailed += async (correlationId, work, error) =>
        {
            var job = work.Job;
            await history.Append(new BackupHistoryRecord
            {
                ProjectId = job.ProjectId, ProjectName = job.ProjectName,
                DatabaseName = job.DatabaseName, Status = "failed",
                CompletedAt = DateTime.UtcNow, ErrorMessage = error,
                CorrelationId = correlationId, ScheduleId = work.ScheduleId,
            });
            overflow.Save(job, error);
        };

        queue.OnDepthReport += snapshot =>
        {
            Log.Information("Scheduler depth: tenant={Tenant} lane={Priority} pending={Pending}",
                snapshot.TenantId, snapshot.Priority, snapshot.PendingCount);
            return Task.CompletedTask;
        };

        return queue;
    });

    services.AddSingleton<TempoScheduler<BackupJob>>(sp =>
    {
        var scheduler = new TempoScheduler<BackupJob>(
            sp.GetRequiredService<TempoQueue<TempoScheduledWork<BackupJob>>>(),
            new TempoSchedulerSettings { TickInterval = TimeSpan.FromSeconds(5), MaxCatchUpSlots = 50 });
        var projects = sp.GetRequiredService<IProjectStore>().GetAllAsync().GetAwaiter().GetResult();
        var count = 0;
        foreach (var p in projects)
        {
            if (p.Database is null || string.IsNullOrWhiteSpace(p.Database.DatabaseName)) continue;
            try
            {
                scheduler.Register(new BackupJob
                {
                    TenantId = BackupJob.TenantIdFromProject(p.Id), ProjectId = p.Id,
                    ProjectName = p.Name, DatabaseName = p.Database.DatabaseName,
                }, new TempoSchedule.Cron("0 2 * * *"), MissedRunPolicy.RunOnce, OverlapPolicy.Skip);
                count++;
            }
            catch (Exception ex) { Log.Error(ex, "Failed to register backup schedule for {Project}", p.Name); }
        }
        Log.Information("Scheduler: {Count} backup schedules registered", count);
        return scheduler;
    });

    services.AddHostedService<SchedulerHostedService>();

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

static void RegisterDesktopServices(IServiceCollection services, string dataDir)
{
    services.AddSingleton<SqliteProjectStore>(_ => new SqliteProjectStore(dataDir));
    services.AddSingleton<IProjectStore>(sp =>
        new DockerCredentialPreservingProjectStore(sp.GetRequiredService<SqliteProjectStore>()));
    services.AddSingleton<IBuildStore, JsonBuildStore>();
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
    services.AddSingleton<IServerStore, JsonServerStore>();
    services.AddSingleton<SshKeyStore>(_ => new SshKeyStore(dataDir));
    services.AddSingleton<IRemoteHostProvider, LinuxSshProvider>();
    services.AddSingleton<IMonitoringProvider, LinuxSshMonitoringProvider>();
    services.AddSchedulerModule();
    services.AddWatchBranchModule();
    services.AddSingleton<IDockerRegistryResourceStore, SqliteDockerRegistryResourceStore>();
    services.AddSingleton<IScriptResourceStore, SqliteScriptResourceStore>();
    services.AddSingleton<ICredentialResourceStore, SqliteCredentialResourceStore>();
    services.AddSingleton<IPipelineResourceStore, SqlitePipelineResourceStore>();
    services.AddSingleton<ResourceResolutionService>();
    services.AddSingleton<ScriptExecutor>();
}
