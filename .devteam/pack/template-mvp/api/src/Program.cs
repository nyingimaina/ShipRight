using MyApp.Api.Features.Auth;
using MyApp.Api.Features.Customers;
using MyApp.Api.Shared.Config;
using MyApp.Api.Shared.Http;
using MyApp.Api.Shared.Logging;
using MyApp.Api.Shared.Storage;

// Running from a terminal: pick up the .env file, so settings such as AUTH_MODE work the way they do in Docker (real environment variables still win).
if (Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") != "true") DotEnv.Load(Directory.GetCurrentDirectory());

var builder = WebApplication.CreateBuilder(args);
builder.AddAppLogging(); // rolling log files (logs/api-DATE.log and .json) as well as the console

// Fast-MVP version: data lives in files under the data folder, no database needed. See README.md for how to add a database later.
builder.Services.AddFileStorage(builder.Configuration, builder.Environment);
try { builder.Services.AddAuthFeature(builder.Environment, builder.Configuration); }
catch (AuthSetupException setup) when (!builder.Environment.IsEnvironment("Testing")) // a setting to change, not a crash: explain it and stop (tests see the exception)
{
    Console.Error.WriteLine(setup.Message);
    Environment.Exit(1);
}
builder.Services.AddCustomersFeature();
// An unexpected error answers with an error id (the same id is in the log), never with the details.
builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context => context.ProblemDetails.Extensions["errorId"] = context.HttpContext.TraceIdentifier);

var app = builder.Build();

app.UseExceptionHandler();   // unexpected errors become a generic RFC 7807 response with an error id; details go to the log, not the client
app.UseAppRequestLogging();  // one line per request in the log
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();

try { await app.EnsureLocalOwnerAsync(); }
catch (AuthSetupException setup) when (!app.Environment.IsEnvironment("Testing"))
{
    Console.Error.WriteLine(setup.Message);
    Environment.Exit(1);
}

app.LogStartup($"files in {app.Services.GetRequiredService<FileStorage>().Folder}");

// Anyone can ask whether the service is up (used by Docker Compose and monitors). It reveals nothing else.
app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();

var api = app.MapGroup("/api").AddEndpointFilter<CsrfFilter>();
api.MapAuthEndpoints();
api.MapCustomerEndpoints();

app.Run();

// Lets the tests start the whole app in memory.
public partial class Program;
