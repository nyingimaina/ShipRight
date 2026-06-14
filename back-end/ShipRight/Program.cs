using Serilog;
using Serilog.Formatting.Json;
using ShipRight.Modules.Builds;
using ShipRight.Modules.Database;
using ShipRight.Modules.Database.Providers;
using ShipRight.Modules.Filesystem;
using ShipRight.Modules.Projects;
using ShipRight.Modules.RepoMaintenance;
using ShipRight.Modules.Services;
using ShipRight.Modules.Servers;
using ShipRight.Modules.Ssh;
using ShipRight.Modules.System;
using ShipRight.Shared.Events;
using ShipRight.Shared.ProcessRunner;
using ShipRight.Shared.SshRunner;
using ShipRight.Shared.Store;
#if WINDOWS
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
#endif

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
    builder.WebHost.UseUrls("http://127.0.0.1:5200");
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
    builder.Services.AddSingleton<IServerStore, JsonServerStore>();

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
    app.UseDefaultFiles();
    app.UseStaticFiles();

    app.MapHealthRoutes();
    app.MapFsRoutes();
    app.MapProjectRoutes();
    app.MapProjectSummaryRoutes();
    app.MapBuildRoutes();
    app.MapDatabaseRoutes();
    app.MapSshTerminalRoutes();
    app.MapContainerLogRoutes();
    app.MapRepoMaintenanceRoutes();
    app.MapServerRoutes();

    app.MapFallbackToFile("index.html");

    await app.Services.GetRequiredService<IBuildStore>().MarkInterruptedAsync();

    var projectCount = app.Services.GetRequiredService<IProjectStore>().Count;
    var buildCount   = app.Services.GetRequiredService<IBuildStore>().Count;
    Log.Information("ShipRight {Version} starting on port {Port}", "1.3.4", 5200);
    Log.Information("Data directory: {DataDir}", dataDir);
    Log.Information("{ProjectCount} projects, {BuildCount} builds loaded", projectCount, buildCount);

    await app.StartAsync();

#if WINDOWS
    var shutdownRequested = new TaskCompletionSource();

    var trayThread = new Thread(() =>
    {
        using var trayIcon = new NotifyIcon();
        try
        {
            var exePath = Environment.ProcessPath;
            if (exePath != null && File.Exists(exePath))
                trayIcon.Icon = Icon.ExtractAssociatedIcon(exePath);
        }
        catch { trayIcon.Icon = SystemIcons.Application; }
        trayIcon.Icon ??= SystemIcons.Application;
        trayIcon.Text = "ShipRight";

        trayIcon.DoubleClick += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("http://127.0.0.1:5200") { UseShellExecute = true }); }
            catch (Exception ex) { Log.Warning(ex, "Failed to open dashboard"); }
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Dashboard", null, (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("http://127.0.0.1:5200") { UseShellExecute = true }); }
            catch (Exception ex) { Log.Warning(ex, "Failed to open dashboard"); }
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) =>
        {
            trayIcon.Visible = false;
            shutdownRequested.TrySetResult();
            Application.Exit();
        });
        trayIcon.ContextMenuStrip = menu;
        trayIcon.Visible = true;

        Application.Run();
    });
    trayThread.SetApartmentState(ApartmentState.STA);
    trayThread.Start();

    _ = Task.Run(async () =>
    {
        await Task.Delay(1500);
        try { Process.Start(new ProcessStartInfo("http://127.0.0.1:5200") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warning(ex, "Failed to open browser"); }
    });

    await Task.WhenAny(shutdownRequested.Task, app.WaitForShutdownAsync());
    await app.StopAsync();
#else
    await app.WaitForShutdownAsync();
#endif
}
catch (Exception ex)
{
    Log.Fatal(ex, "ShipRight failed to start");
}
finally
{
    Log.CloseAndFlush();
}
