using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using MyApp.Api.Features.Customers;
using MyApp.Api.Tests.Support;

namespace MyApp.Api.Tests;

/// <summary>
/// The app writes what it does to rolling files (logs/api-DATE.log for people, logs/api-DATE.json for DevTeam), so that when something
/// is broken the real behaviour can be read instead of guessed. Secrets and personal data stay out.
/// </summary>
public sealed class LoggingTests
{
    private static string NewLogFolder() => Path.Combine(Path.GetTempPath(), "api-logs-" + Guid.NewGuid().ToString("N")[..8]);

    private static string ReadAll(string folder, string pattern) =>
        Directory.Exists(folder) ? string.Concat(Directory.GetFiles(folder, pattern).Select(f => File.ReadAllText(f) + "\n")) : "";

    /// <summary>Runs a test body against a fresh app, then stops it (which writes out what it logged) and returns the logs folder.</summary>
    private static async Task<string> RunAsync(Func<AppClient, Task> body, Action<IWebHostBuilder>? configure = null)
    {
        var folder = NewLogFolder();
        var storage = await IsolatedApp.CreateAsync();
        try
        {
            var client = storage.Start(b =>
            {
                b.UseSetting("Logging:Directory", folder);
                configure?.Invoke(b);
            });
            await body(client);
        }
        finally { await storage.DisposeAsync(); }
        return folder;
    }

    [Fact]
    public async Task EveryRequestIsLogged_WithItsPathStatusAndDuration()
    {
        var folder = await RunAsync(async client => (await client.GetAsync("/health")).EnsureSuccessStatusCode());

        var json = ReadAll(folder, "api-*.json");
        Assert.Contains("\"RequestPath\":\"/health\"", json);
        Assert.Contains("\"StatusCode\":200", json);
        Assert.Matches("\"Elapsed\":[0-9.]+", json);
        Assert.Contains("/health", ReadAll(folder, "api-*.log"));
    }

    [Fact]
    public async Task AnUnexpectedError_IsInTheLog_WithTheIdTheClientWasGiven()
    {
        string errorId = "";
        var folder = await RunAsync(async client =>
        {
            var response = await client.GetAsync("/api/customers");
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            errorId = Regex.Match(body, "\"errorId\":\"([^\"]+)\"").Groups[1].Value;
            Assert.NotEmpty(errorId);
            Assert.DoesNotContain("Boom was thrown on purpose", body); // the client gets the id, never the details
        }, b => b.ConfigureTestServices(s => s.AddScoped<CustomerRepository>(_ => throw new InvalidOperationException("Boom was thrown on purpose"))));

        var errors = ReadAll(folder, "api-*.json").Split('\n').Where(l => l.Contains("\"@l\":\"Error\"")).ToList();
        Assert.NotEmpty(errors);
        Assert.Contains(errors, l => l.Contains(errorId) && l.Contains("Boom was thrown on purpose"));
    }

    [Fact]
    public async Task PasswordsAndEmailAddresses_AreNeverWritten()
    {
        var password = TestApp.RandomPassword();
        var email = "private.person@example.com";
        var folder = await RunAsync(async client =>
        {
            (await client.RegisterAsync(email, password)).EnsureSuccessStatusCode();
            await client.LoginAsync(email, password);
            await client.LoginAsync(email, "a wrong password");
        }, b => b.UseSetting("Auth:Mode", "multi"));

        var everything = ReadAll(folder, "api-*.json") + ReadAll(folder, "api-*.log");
        Assert.NotEmpty(everything);
        Assert.DoesNotContain(password, everything);
        Assert.DoesNotContain("a wrong password", everything);
        Assert.DoesNotContain(email, everything);
    }

    [Fact]
    public async Task LogFiles_RollDaily_AndAreNamedSoTheyCanBeFound()
    {
        var folder = await RunAsync(async client => (await client.GetAsync("/health")).EnsureSuccessStatusCode());

        var names = Directory.GetFiles(folder).Select(Path.GetFileName).ToList();
        Assert.Contains(names, n => Regex.IsMatch(n!, @"^api-\d{8}\.log$"));
        Assert.Contains(names, n => Regex.IsMatch(n!, @"^api-\d{8}\.json$"));
    }

    [Fact]
    public async Task StartUp_IsLogged_WithTheModeTheAppRunsIn()
    {
        var folder = await RunAsync(async client => (await client.GetAsync("/health")).EnsureSuccessStatusCode(), b => b.UseSetting("Auth:Mode", "single"));

        Assert.Contains("Started", ReadAll(folder, "api-*.log"));
        Assert.Contains("single", ReadAll(folder, "api-*.log"));
    }

}
