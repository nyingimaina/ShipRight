using Serilog;
using Serilog.Formatting.Json;
using ShipRight.Modules.Builds;
using ShipRight.Modules.Database;
using ShipRight.Modules.Database.Providers;
using ShipRight.Modules.Filesystem;
using ShipRight.Modules.Projects;
using ShipRight.Modules.RepoMaintenance;
using ShipRight.Modules.System;
using ShipRight.Shared.Events;
using ShipRight.Shared.ProcessRunner;
using ShipRight.Shared.SshRunner;
using ShipRight.Shared.Store;

var dataDir = DataDirectory.Resolve();

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
    builder.Host.UseSerilog();

    builder.Services.ConfigureHttpJsonOptions(o =>
        o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

    var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
        ?? ["http://localhost:5200", "http://127.0.0.1:5200"];

    builder.Services.AddCors(options =>
        options.AddPolicy("ShipRightPolicy", policy =>
            policy.WithOrigins(allowedOrigins)
                  .AllowAnyHeader()
                  .AllowAnyMethod()));

    builder.Services.AddSingleton<IProjectStore, JsonProjectStore>();
    builder.Services.AddSingleton<IBuildStore, JsonBuildStore>();
    builder.Services.AddSingleton<BuildEventBus>();
    builder.Services.AddSingleton<IProcessRunner, ProcessRunner>();
    builder.Services.AddSingleton<KnownHostsStore>();
    builder.Services.AddSingleton<ISshRunner, SshRunner>();
    builder.Services.AddSingleton<BuildOrchestrator>();
    builder.Services.AddSingleton<MariaDbProvider>();
    builder.Services.AddSingleton<SqlServerProvider>();
    builder.Services.AddSingleton<IDbProviderResolver>(sp => new DbProviderResolver(
        sp.GetRequiredService<MariaDbProvider>(),
        sp.GetRequiredService<SqlServerProvider>()));
    builder.Services.AddSingleton<DatabaseOrchestrator>();

    var app = builder.Build();

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

    // v2: Insert JWT/session auth middleware here
    app.UseCors("ShipRightPolicy");
    app.UseStaticFiles();

    app.MapHealthRoutes();
    app.MapFsRoutes();
    app.MapProjectRoutes();
    app.MapProjectSummaryRoutes();
    app.MapBuildRoutes();
    app.MapDatabaseRoutes();
    app.MapRepoMaintenanceRoutes();

    app.MapFallbackToFile("index.html");

    await app.Services.GetRequiredService<IBuildStore>().MarkInterruptedAsync();

    var projectCount = app.Services.GetRequiredService<IProjectStore>().Count;
    var buildCount   = app.Services.GetRequiredService<IBuildStore>().Count;
    Log.Information("ShipRight {Version} starting on port {Port}", "1.0.0", 5200);
    Log.Information("Data directory: {DataDir}", dataDir);
    Log.Information("{ProjectCount} projects, {BuildCount} builds loaded", projectCount, buildCount);

    app.Run("http://127.0.0.1:5200");
}
catch (Exception ex)
{
    Log.Fatal(ex, "ShipRight failed to start");
}
finally
{
    Log.CloseAndFlush();
}
