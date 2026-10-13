using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Rocket.Libraries.DatabaseIntegrator;
using ShipRight.Database;
using ShipRight.Modules.Auth.Services;
using ShipRight.Modules.Identity;
using ShipRight.Tests.Modules.Tenancy;

namespace ShipRight.Tests.Modules.Identity;

// The real cloud app (Program.cs in cloud mode) started in memory with a stand-in for the database, so the HTTP
// behaviour (cookies, 401/403, public endpoints, roles, company from the sign-in) is checked on any machine.
// SQL itself is covered by the stand-in's recorded statements and by the MariaDB suite (CloudApiIdentityTests).
public sealed class InMemoryCloudFactory : WebApplicationFactory<Program>
{
    private static int _clientNumber;
    private readonly Dictionary<string, string?> _saved = new();

    public RecordingDatabaseHelper Db { get; } = new();
    public RecordingEmailSender Email { get; } = new();
    public InMemoryIdentityRepository Users { get; }

    // A header the test middleware turns into an external (Google) sign-in cookie, standing in for Google's callback.
    public const string TestExternalIdentityHeader = "X-Test-External-Identity";

    // Google is off by default so the common cloud tests stay provider-free; pass true to exercise the Google callback.
    public InMemoryCloudFactory(bool google = false)
    {
        Users = new InMemoryIdentityRepository(Db);
        Set("SHIPRIGHT__MODE", "cloud");
        Set("SHIPRIGHT__DB_CONNECTION", "Server=db;Database=none;User Id=x;Password=not-used;");
        Set("SHIPRIGHT__CORS_ORIGINS", "https://app.example.test");
        Set("SHIPRIGHT__PUBLIC_URL", "https://app.example.test");
        Set("SHIPRIGHT__GOOGLE_CLIENT_ID", google ? "test-google-client-id" : null);
        Set("SHIPRIGHT__GOOGLE_CLIENT_SECRET", google ? "test-google-client-secret" : null);
        Set("SHIPRIGHT_DATA_DIR", Path.Combine(Path.GetTempPath(), "shipright-memory-test-" + Guid.NewGuid().ToString("N")[..8]));
    }

    private void Set(string name, string? value)
    {
        _saved[name] = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.ConfigureTestServices(services =>
        {
            services.Replace(ServiceDescriptor.Singleton<IDatabaseHelper<Guid>>(Db));
            services.Replace(ServiceDescriptor.Singleton<IdentityRepository>(Users));
            services.Replace(ServiceDescriptor.Singleton<MigrationRunner>(
                new NoMigrations(Options.Create(new DatabaseConnectionSettings()))));
            services.Replace(ServiceDescriptor.Singleton<IPasswordResetEmailSender>(Email));
            services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
            services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.Zero);
            services.AddSingleton<IStartupFilter>(new TestIpStartupFilter());
            services.AddSingleton<IStartupFilter>(new TestExternalIdentityStartupFilter());
        });

    public ApiClient NewClient()
    {
        var http = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        var n = Interlocked.Increment(ref _clientNumber);
        http.DefaultRequestHeaders.Add(TestIpStartupFilter.Header, $"10.{(n >> 16) & 255}.{(n >> 8) & 255}.{n & 255}");
        return new ApiClient(http);
    }

    // Puts a person straight into the stand-in database, as if they already existed.
    public AppUser Seed(string email, string role = AppRoles.Admin, string password = "long-enough-1", Guid? companyId = null,
        bool active = true, bool mustChange = false, string? passwordHash = null)
    {
        var user = new AppUser
        {
            Id = Guid.NewGuid(),
            CompanyId = companyId ?? Guid.NewGuid(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            Name = email,
            Role = role,
            IsActive = active,
            MustChangePassword = mustChange,
        };
        user.PasswordHash = passwordHash ?? new LegacyAwarePasswordHasher().HashPassword(user, password);
        Users.Put(user);
        return user;
    }

    protected override void Dispose(bool disposing)
    {
        try { base.Dispose(disposing); }
        catch (ArgumentNullException) { /* the host never finished starting; nothing to stop */ }
        if (!disposing) return;
        foreach (var (name, value) in _saved) Environment.SetEnvironmentVariable(name, value);
    }

    private sealed class NoMigrations(IOptions<DatabaseConnectionSettings> settings)
        : MigrationRunner(settings, NullLogger<MigrationRunner>.Instance)
    {
        public override Task RunAsync(CancellationToken ct = default) => Task.CompletedTask;
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

    // Plants the external (Google) identity cookie Google's real callback would have set, so the callback endpoint can
    // be driven end to end without talking to Google. Header format: subject|email|name|email_verified.
    private sealed class TestExternalIdentityStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            app =>
            {
                app.Use(async (context, nextMiddleware) =>
                {
                    if (!context.Request.Headers.TryGetValue(TestExternalIdentityHeader, out var spec))
                    {
                        await nextMiddleware();
                        return;
                    }

                    var parts = spec.ToString().Split('|');
                    var claims = new List<Claim>
                    {
                        new(ClaimTypes.NameIdentifier, parts[0]),
                        new(ClaimTypes.Email, parts[1]),
                        new(ClaimTypes.Name, parts[2]),
                        new("email_verified", parts.Length > 3 ? parts[3] : "true"),
                    };
                    await context.SignInAsync(IdentityConstants.ExternalScheme,
                        new ClaimsPrincipal(new ClaimsIdentity(claims, "External")));
                    context.Response.StatusCode = StatusCodes.Status204NoContent;
                });
                next(app);
            };
    }
}

// People and logins kept in a dictionary, behaving like the unique-email index and the per-row updates of the real tables.
public sealed class InMemoryIdentityRepository(IDatabaseHelper<Guid> db) : IdentityRepository(db)
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, AppUser> _users = new();
    private readonly Dictionary<(string Provider, string Key), Guid> _logins = new();

    public int CompanyCount { get; private set; }
    public List<string> CompanyNames { get; } = [];

    public void Put(AppUser user)
    {
        lock (_gate) _users[user.Id] = Copy(user);
    }

    public AppUser? Peek(string email)
    {
        lock (_gate) return _users.Values.Where(u => u.NormalizedEmail == email.ToUpperInvariant()).Select(Copy).SingleOrDefault();
    }

    public AppUser? Peek(Guid id)
    {
        lock (_gate) return _users.TryGetValue(id, out var user) ? Copy(user) : null;
    }

    public int UserCount(Guid companyId)
    {
        lock (_gate) return _users.Values.Count(u => u.CompanyId == companyId);
    }

    public override Task<AppUser?> FindByIdAsync(Guid id) => Task.FromResult(Peek(id));

    public override Task<AppUser?> FindByNormalizedEmailAsync(string normalizedEmail)
    {
        lock (_gate) return Task.FromResult(_users.Values.Where(u => u.NormalizedEmail == normalizedEmail).Select(Copy).SingleOrDefault());
    }

    public override Task<AppUser?> FindByLoginAsync(string provider, string providerKey)
    {
        lock (_gate)
            return Task.FromResult(_logins.TryGetValue((provider, providerKey), out var id) ? Peek(id) : null);
    }

    public override Task<bool> TryInsertUserAsync(AppUser user)
    {
        lock (_gate)
        {
            if (_users.Values.Any(u => u.NormalizedEmail == user.NormalizedEmail)) return Task.FromResult(false);
            _users[user.Id] = Copy(user);
            return Task.FromResult(true);
        }
    }

    public override Task UpdateCredentialsAsync(AppUser user)
    {
        lock (_gate)
        {
            // Mirrors the real UPDATE: credentials only, never the role, company or active flag.
            if (!_users.TryGetValue(user.Id, out var row)) return Task.CompletedTask;
            row.Email = user.Email;
            row.NormalizedEmail = user.NormalizedEmail;
            row.Name = user.Name;
            row.PasswordHash = user.PasswordHash;
            row.MustChangePassword = user.MustChangePassword;
            row.SecurityStamp = user.SecurityStamp;
            row.LockoutEnd = user.LockoutEnd;
            row.AccessFailedCount = user.AccessFailedCount;
        }
        return Task.CompletedTask;
    }

    public override Task<bool> TryCreateCompanyWithUserAsync(string companyName, AppUser user)
    {
        lock (_gate)
        {
            if (_users.Values.Any(u => u.NormalizedEmail == user.NormalizedEmail)) return Task.FromResult(false);
            user.CompanyId = Guid.NewGuid();
            CompanyCount++;
            CompanyNames.Add(companyName);
            _users[user.Id] = Copy(user);
            return Task.FromResult(true);
        }
    }

    public override Task AddLoginAsync(Guid userId, string provider, string providerKey)
    {
        lock (_gate) _logins[(provider, providerKey)] = userId;
        return Task.CompletedTask;
    }

    // What an Admin's switch-off does in the real table: the flag and a new security stamp.
    public void SwitchOff(Guid id)
    {
        lock (_gate)
        {
            _users[id].IsActive = false;
            _users[id].SecurityStamp = Guid.NewGuid().ToString("N");
        }
    }

    private static AppUser Copy(AppUser u) => new()
    {
        Id = u.Id, CompanyId = u.CompanyId, Email = u.Email, NormalizedEmail = u.NormalizedEmail, Name = u.Name,
        PasswordHash = u.PasswordHash, Role = u.Role, IsActive = u.IsActive, MustChangePassword = u.MustChangePassword,
        SecurityStamp = u.SecurityStamp, LockoutEnd = u.LockoutEnd, AccessFailedCount = u.AccessFailedCount,
        Created = u.Created, Modified = u.Modified,
    };
}
