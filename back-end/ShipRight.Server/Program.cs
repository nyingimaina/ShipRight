using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Reflection;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Dapper.Contrib.Extensions;
using Jattac.Libs.Tempo;
using Jattac.Libs.Tempo.Scheduling;
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
using ShipRight.Server;

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

    var allowedOrigins = cloudMode
        ? CloudConfiguration.ResolveAllowedOrigins(
            builder.Configuration,
            Environment.GetEnvironmentVariable("SHIPRIGHT__CORS_ORIGINS"))
        : ["http://localhost:5200", "http://127.0.0.1:5200"];

    if (cloudMode)
    {
        CloudConfiguration.ValidateProductionSettings(
            Environment.GetEnvironmentVariable("SHIPRIGHT__DB_CONNECTION"),
            Environment.GetEnvironmentVariable("SHIPRIGHT__JWT_KEY"),
            Environment.GetEnvironmentVariable("SHIPRIGHT__ADMIN_EMAIL"),
            Environment.GetEnvironmentVariable("SHIPRIGHT__ADMIN_PASSWORD"),
            Environment.GetEnvironmentVariable("SHIPRIGHT__CORS_ORIGINS"));
    }

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
        CloudDiRegistrar.Register(builder.Services, builder.Configuration);
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

    app.MapHealthRoutes(cloudMode ? AppMode.Cloud : AppMode.Desktop);
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
    if (cloudMode)
    {
        app.MapSchedulerCoreRoutes();
        app.MapCloudSchedulerHistoryRoutes();
    }
    else
    {
        app.MapSchedulerRoutes();
    }
    app.MapSshKeyRoutes();
    app.MapServerSshKeyRoutes();
    if (cloudMode)
    {
        app.MapWatchBranchCoreRoutes();
        app.MapCloudWatchBranchHistoryRoutes();
    }
    else
    {
        app.MapWatchBranchRoutes();
    }
    app.MapResourceRoutes();
    app.MapPipelineRoutes();

    app.MapFallbackToFile("index.html");

    // ─── Startup ───────────────────────────────────────────────────

    if (cloudMode)
    {
        _ = app.Services.GetRequiredService<IPasswordResetEmailSender>();
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

static void RegisterDesktopServices(IServiceCollection services, string dataDir)
{
    services.AddSingleton<SqliteProjectStore>(_ => new SqliteProjectStore(dataDir));
    services.AddSingleton<IProjectStore>(sp =>
        new DockerCredentialPreservingProjectStore(sp.GetRequiredService<SqliteProjectStore>()));
    services.AddSingleton<IBuildStore, SqliteBuildStore>();
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
    services.AddSingleton<IServerStore, SqliteServerStore>();
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
