using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace MyApp.Api.Shared.Logging;

/// <summary>
/// What the app does, written down where it can be found: logs/api-DATE.log for people (readable lines) and logs/api-DATE.json for DevTeam
/// (one JSON object per line). Both roll every day and by size, and old ones are deleted after two weeks. Every request is one line
/// (path, status, how long); an unexpected error is recorded with its full details and an error id, and the client is only given the id.
/// Never log a password, a token, a request body or an email address.
/// </summary>
public static class AppLogging
{
    private const int KeepFiles = 14;
    private const long MaxFileBytes = 20 * 1024 * 1024;
    private const string LineTemplate = "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3} [{RequestId}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

    public static WebApplicationBuilder AddAppLogging(this WebApplicationBuilder builder)
    {
        // logs/ next to where the app is run from (the app's main folder when started with: dotnet run --project api/src)
        var directory = Path.GetFullPath(builder.Configuration["Logging:Directory"] is { Length: > 0 } configured ? configured : "logs");
        var level = LevelFrom(builder.Configuration["LOG_LEVEL"] ?? builder.Configuration["Logging:Level"], builder.Environment.IsProduction() ? LogEventLevel.Information : LogEventLevel.Debug);

        builder.Host.UseSerilog((context, _, logger) => logger
            .MinimumLevel.Is(level)
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", "MyApp.Api")
            .Enrich.WithProperty("Environment", context.HostingEnvironment.EnvironmentName)
            .WriteTo.Console(outputTemplate: LineTemplate)
            .WriteTo.File(Path.Combine(directory, "api-.log"), outputTemplate: LineTemplate, rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: KeepFiles, fileSizeLimitBytes: MaxFileBytes, rollOnFileSizeLimit: true, shared: true)
            .WriteTo.File(new CompactJsonFormatter(), Path.Combine(directory, "api-.json"), rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: KeepFiles, fileSizeLimitBytes: MaxFileBytes, rollOnFileSizeLimit: true, shared: true));
        return builder;
    }

    /// <summary>One line per request, with who (the owner id, never the email) and the error id that an error response carries.</summary>
    public static WebApplication UseAppRequestLogging(this WebApplication app)
    {
        app.UseSerilogRequestLogging(options => options.EnrichDiagnosticContext = (diagnostics, http) =>
        {
            diagnostics.Set("RequestId", http.TraceIdentifier);
            if (http.User.Identity?.IsAuthenticated == true) diagnostics.Set("UserId", http.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "");
        });
        return app;
    }

    /// <summary>The first lines of every run: what the app is, how people are known and where its data is. Answers "which settings was it running with?".</summary>
    public static void LogStartup(this WebApplication app, string storage)
    {
        app.Lifetime.ApplicationStarted.Register(() =>
            app.Logger.LogInformation("Started in {EnvironmentName} mode. Sign-in: {AuthMode}. Data: {Storage}. Logs: {LogFolder}",
                app.Environment.EnvironmentName, Features.Auth.AuthMode.IsSingle(app.Configuration) ? "single (no sign-in)" : "multi (people sign in)", storage,
                Path.GetFullPath(app.Configuration["Logging:Directory"] is { Length: > 0 } d ? d : "logs")));
    }

    private static LogEventLevel LevelFrom(string? text, LogEventLevel fallback) =>
        Enum.TryParse<LogEventLevel>(text, ignoreCase: true, out var level) ? level : fallback;
}
