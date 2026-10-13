using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ShipRight.Modules.Auth.Services;

namespace ShipRight.Tests.Modules.Identity;

// The real cloud app (Program.cs in cloud mode) started in memory against a throw-away MariaDB database.
public sealed class CloudApiFactory : WebApplicationFactory<Program>
{
    private static int _clientNumber;

    private readonly MariaDbTestDatabase _database;
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "shipright-test-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly Dictionary<string, string?> _saved = new();

    public RecordingEmailSender Email { get; } = new();

    public CloudApiFactory(MariaDbTestDatabase database)
    {
        _database = database;
        Set("SHIPRIGHT__MODE", "cloud");
        Set("SHIPRIGHT__DB_CONNECTION", database.ConnectionString);
        Set("SHIPRIGHT__CORS_ORIGINS", "https://app.example.test");
        Set("SHIPRIGHT__PUBLIC_URL", "https://app.example.test");
        Set("SHIPRIGHT_DATA_DIR", _dataDir);
        Set("SHIPRIGHT__GOOGLE_CLIENT_ID", null);
        Set("SHIPRIGHT__GOOGLE_CLIENT_SECRET", null);
    }

    private void Set(string name, string? value)
    {
        _saved[name] = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.ConfigureTestServices(services =>
        {
            services.Replace(ServiceDescriptor.Singleton<IPasswordResetEmailSender>(Email));
            services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
            // A signed-in person's account is rechecked on every request, so role and switch-off changes show at once.
            services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.Zero);
            // Every test client looks like a different machine, so the per-address rate limits do not interfere.
            services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter>(new TestIpStartupFilter());
        });

    public ApiClient NewClient()
    {
        var http = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        var n = Interlocked.Increment(ref _clientNumber);
        http.DefaultRequestHeaders.Add(TestIpStartupFilter.Header, $"10.{(n >> 16) & 255}.{(n >> 8) & 255}.{n & 255}");
        return new ApiClient(http);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        foreach (var (name, value) in _saved) Environment.SetEnvironmentVariable(name, value);
        try { Directory.Delete(_dataDir, recursive: true); } catch { /* temp folder, best effort */ }
    }

    private sealed class TestIpStartupFilter : IStartupFilter
    {
        public const string Header = "X-Test-Ip";

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            app =>
            {
                app.Use((context, nextMiddleware) =>
                {
                    if (IPAddress.TryParse(context.Request.Headers[Header], out var address))
                        context.Connection.RemoteIpAddress = address;
                    return nextMiddleware();
                });
                next(app);
            };
    }
}

public sealed class RecordingEmailSender : IPasswordResetEmailSender
{
    private readonly List<(string Recipient, string Token)> _sent = [];

    public IReadOnlyList<(string Recipient, string Token)> Sent
    {
        get { lock (_sent) return _sent.ToList(); }
    }

    public Task SendAsync(string recipient, string token, CancellationToken cancellationToken = default)
    {
        lock (_sent) _sent.Add((recipient, token));
        return Task.CompletedTask;
    }
}

// One signed-in (or not) browser: keeps its cookie, and wraps requests so tests read like the user's steps.
public sealed class ApiClient(HttpClient http)
{
    public HttpClient Http { get; } = http;

    public async Task<ApiResult> Send(HttpMethod method, string url, object? body = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await Http.SendAsync(request);
        return new ApiResult(response.StatusCode, await response.Content.ReadAsStringAsync(), response.Content.Headers.ContentType?.MediaType);
    }

    public Task<ApiResult> Get(string url) => Send(HttpMethod.Get, url);
    public Task<ApiResult> Post(string url, object? body = null) => Send(HttpMethod.Post, url, body ?? new { });
    public Task<ApiResult> Put(string url, object body) => Send(HttpMethod.Put, url, body);
    public Task<ApiResult> Delete(string url) => Send(HttpMethod.Delete, url);

    public Task<ApiResult> Register(string email, string password = "long-enough-1", string name = "Test Person", string? company = "Test Co") =>
        Post("/api/auth/register", new { email, password, name, companyName = company });

    public Task<ApiResult> Login(string email, string password = "long-enough-1") =>
        Post("/api/auth/login", new { email, password });
}

public sealed record ApiResult(HttpStatusCode Status, string Body, string? ContentType)
{
    public JsonElement Json => JsonDocument.Parse(Body).RootElement;
    public string? Text(params string[] path)
    {
        var node = Json;
        foreach (var part in path)
            if (!node.TryGetProperty(part, out node)) return null;
        return node.ValueKind == JsonValueKind.String ? node.GetString() : node.ToString();
    }
    public bool HasFieldError(string field) =>
        Json.TryGetProperty("errors", out var errors) && errors.TryGetProperty(field, out var messages) && messages.GetArrayLength() > 0;
}
