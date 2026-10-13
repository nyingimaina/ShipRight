using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using ShipRight.Shared.Store;
using ShipRight.Shared.Tenancy;

namespace ShipRight.Modules.Identity;

// Everything the cloud server needs for sign-in: Identity on the project's own SQL access (no EF Core),
// a 7-day cookie session, Google through the framework's OAuth handler, and one fallback authorization policy.
public static class IdentityRegistration
{
    public const string CookieName = "shipright.auth";
    public static readonly TimeSpan SessionLength = TimeSpan.FromDays(7);
    public static readonly TimeSpan StampCheckInterval = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan LockoutLength = TimeSpan.FromMinutes(5);
    public const int MaxFailedAttempts = 5;

    public static void AddShipRightIdentity(this IServiceCollection services, GoogleSettings google)
    {
        services.AddHttpContextAccessor();
        services.AddSingleton<ICompanyContext, CompanyContext>();
        services.AddSingleton<CompanyScope>();
        services.AddSingleton(google);

        services.AddSingleton<IdentityRepository>();
        services.AddSingleton<TeamRepository>();
        services.AddScoped<DapperUserStore>();
        services.AddScoped<RegistrationService>();

        // Registered before AddIdentityCore so the legacy-aware hasher wins over the default one.
        services.AddScoped<IPasswordHasher<AppUser>, LegacyAwarePasswordHasher>();

        services.AddIdentityCore<AppUser>(options =>
            {
                options.Password.RequiredLength = IdentityValidation.MinPasswordLength;
                options.Password.RequireDigit = false;
                options.Password.RequireLowercase = false;
                options.Password.RequireUppercase = false;
                options.Password.RequireNonAlphanumeric = false;
                options.User.RequireUniqueEmail = true;
                options.User.AllowedUserNameCharacters = string.Empty; // the user name is the email address
                options.Lockout.AllowedForNewUsers = true;
                options.Lockout.MaxFailedAccessAttempts = MaxFailedAttempts;
                options.Lockout.DefaultLockoutTimeSpan = LockoutLength;
                options.SignIn.RequireConfirmedEmail = false;
            })
            .AddUserStore<DapperUserStore>()
            .AddClaimsPrincipalFactory<AppClaimsPrincipalFactory>()
            .AddSignInManager()
            .AddTokenProvider<DataProtectorTokenProvider<AppUser>>(TokenOptions.DefaultProvider);

        services.Configure<DataProtectionTokenProviderOptions>(o => o.TokenLifespan = TimeSpan.FromHours(1));
        services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = StampCheckInterval);

        // Keys live in the data folder so cookies and reset links survive a restart or a new container.
        services.AddDataProtection()
            .SetApplicationName("ShipRight")
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(DataDirectory.Resolve(), "keys")));

        var authentication = services.AddAuthentication(IdentityConstants.ApplicationScheme);
        authentication.AddIdentityCookies();
        if (google.Enabled)
            authentication.AddOAuth(GoogleSettings.Scheme, o => GoogleSignIn.Configure(o, google));

        services.ConfigureApplicationCookie(options =>
        {
            options.Cookie.Name = CookieName;
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.ExpireTimeSpan = SessionLength;
            options.SlidingExpiration = false;
            options.Events.OnRedirectToLogin = context => WriteJson(context.Response, StatusCodes.Status401Unauthorized, "Sign in required.");
            options.Events.OnRedirectToAccessDenied = context => WriteJson(context.Response, StatusCodes.Status403Forbidden, "You do not have permission to do that.");
        });

        services.AddSingleton<IAuthorizationHandler, RoleAuthorizationHandler>();
        services.AddAuthorization(options =>
            options.FallbackPolicy = new AuthorizationPolicyBuilder().AddRequirements(new RoleAccessRequirement()).Build());
    }

    private static Task WriteJson(HttpResponse response, int status, string message)
    {
        response.StatusCode = status;
        return response.WriteAsJsonAsync(new { message, isError = true });
    }
}
