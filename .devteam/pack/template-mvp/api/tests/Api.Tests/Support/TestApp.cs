using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using MyApp.Api.Features.Auth;

namespace MyApp.Api.Tests.Support;

/// <summary>Starts the whole API in memory with its data in a scratch folder. Shared by every test class (see TestCollection).</summary>
public sealed class TestApp : IAsyncLifetime
{
    private string? _dataFolder;
    private WebApplicationFactory<Program>? _factory;

    public Task InitializeAsync()
    {
        _dataFolder = Directory.CreateTempSubdirectory("api-tests-").FullName;
        _factory = StartApp(_dataFolder);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();
        if (_dataFolder is not null) Directory.Delete(_dataFolder, true);
    }

    /// <summary>The whole API in memory, keeping its data in the given folder. Starting a second one on the same folder is a restart.</summary>
    public static WebApplicationFactory<Program> StartApp(string dataFolder)
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Data:Directory", dataFolder);
            builder.UseSetting("Auth:Mode", "multi"); // these tests are about people signing in; SingleUserTests covers the default
        });
        _ = factory.Server; // starts the app
        return factory;
    }

    /// <summary>A browser-like client with its own cookie jar.</summary>
    public AppClient NewClient() => new(_factory!.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true }));

    /// <summary>A client with a freshly registered, signed-in user.</summary>
    public async Task<AppClient> NewSignedInClientAsync()
    {
        var client = NewClient();
        var response = await client.RegisterAsync(UniqueEmail(), RandomPassword());
        response.EnsureSuccessStatusCode();
        return client;
    }

    public static string UniqueEmail() => $"user-{Guid.NewGuid():N}@example.com";

    // Generated, so no credential-looking literal sits in the source (SEC-001).
    public static string RandomPassword() => "Pw-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
}

/// <summary>A data folder and apps of a test's own, started in whichever settings it needs. Starting another app on the same folder is a restart.</summary>
public sealed class IsolatedApp : IAsyncDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("api-isolated-").FullName;
    private readonly List<WebApplicationFactory<Program>> _factories = [];

    public static Task<IsolatedApp> CreateAsync() => Task.FromResult(new IsolatedApp());

    public AppClient Start(Action<IWebHostBuilder> configure)
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Data:Directory", _folder);
            configure(builder);
        });
        _ = factory.Server;
        _factories.Add(factory);
        return new AppClient(factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true }));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var factory in _factories) await factory.DisposeAsync();
        Directory.Delete(_folder, true);
    }
}

/// <summary>One data folder and one app for all tests; tests stay independent by using their own users and customers.</summary>
[CollectionDefinition(Name)]
public sealed class TestCollection : ICollectionFixture<TestApp>
{
    public const string Name = "api";
}

/// <summary>An HttpClient that knows the CSRF dance: every write first fetches a fresh token and sends it in the header.</summary>
public sealed class AppClient(HttpClient http)
{
    public HttpClient Http => http;

    public Task<HttpResponseMessage> GetAsync(string url) => http.GetAsync(url);

    public Task<HttpResponseMessage> PostAsync(string url, object? body = null) => SendAsync(HttpMethod.Post, url, body);

    public Task<HttpResponseMessage> PutAsync(string url, object? body) => SendAsync(HttpMethod.Put, url, body);

    public Task<HttpResponseMessage> DeleteAsync(string url) => SendAsync(HttpMethod.Delete, url, null);

    public Task<HttpResponseMessage> RegisterAsync(string email, string password) =>
        PostAsync("/api/auth/register", new RegisterRequest(email, password));

    public Task<HttpResponseMessage> LoginAsync(string email, string password) =>
        PostAsync("/api/auth/login", new LoginRequest(email, password));

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? body)
    {
        var token = (await http.GetFromJsonAsync<CsrfDto>("/api/auth/csrf"))!.Token;
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-CSRF-TOKEN", token);
        if (body is not null) request.Content = JsonContent.Create(body);
        return await http.SendAsync(request);
    }
}
