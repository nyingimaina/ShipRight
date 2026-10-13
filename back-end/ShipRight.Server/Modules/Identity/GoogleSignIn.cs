using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Identity;
using Rocket.Libraries.DatabaseIntegrator;

namespace ShipRight.Modules.Identity;

// Google settings are optional: with no client id and secret the Google button is hidden and email sign-in is unaffected.
public sealed record GoogleSettings(string? ClientId, string? ClientSecret)
{
    public const string Scheme = "Google";
    public bool Enabled => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);

    public static GoogleSettings FromEnvironment() => new(
        Environment.GetEnvironmentVariable("SHIPRIGHT__GOOGLE_CLIENT_ID"),
        Environment.GetEnvironmentVariable("SHIPRIGHT__GOOGLE_CLIENT_SECRET"));
}

public static class GoogleSignIn
{
    private const string FailedRedirect = "/login?error=google_failed";
    private const string CancelledRedirect = "/login?error=google_cancelled";
    private const string SwitchedOffRedirect = "/login?error=switched_off";
    private const string CompleteUrl = "/api/auth/google/complete";

    // Google's own endpoints through the framework's generic OAuth handler: no extra package.
    public static void Configure(OAuthOptions options, GoogleSettings settings)
    {
        options.ClientId = settings.ClientId!;
        options.ClientSecret = settings.ClientSecret!;
        options.AuthorizationEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
        options.TokenEndpoint = "https://oauth2.googleapis.com/token";
        options.UserInformationEndpoint = "https://openidconnect.googleapis.com/v1/userinfo";
        options.CallbackPath = "/api/auth/google/callback";
        options.SignInScheme = IdentityConstants.ExternalScheme;
        options.UsePkce = true;
        options.SaveTokens = false;
        options.Scope.Add("openid");
        options.Scope.Add("email");
        options.Scope.Add("profile");
        options.ClaimActions.MapJsonKey(ClaimTypes.NameIdentifier, "sub");
        options.ClaimActions.MapJsonKey(ClaimTypes.Email, "email");
        options.ClaimActions.MapJsonKey(ClaimTypes.Name, "name");
        options.ClaimActions.MapJsonKey("email_verified", "email_verified");
        options.Events = new OAuthEvents
        {
            OnCreatingTicket = async context =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, context.Options.UserInformationEndpoint);
                request.Headers.Authorization = new("Bearer", context.AccessToken);
                using var response = await context.Backchannel.SendAsync(request, context.HttpContext.RequestAborted);
                response.EnsureSuccessStatusCode();
                using var json = await global::System.Text.Json.JsonDocument.ParseAsync(
                    await response.Content.ReadAsStreamAsync(context.HttpContext.RequestAborted),
                    cancellationToken: context.HttpContext.RequestAborted);
                context.RunClaimActions(json.RootElement);
            },
            // Cancelled or failed at Google: back to the sign-in screen with a plain message. The error text is dropped
            // on purpose, because remote failures can carry codes and tokens.
            OnRemoteFailure = context =>
            {
                context.Response.Redirect(CancelledRedirect);
                context.HandleResponse();
                return Task.CompletedTask;
            },
        };
    }

    public static void MapGoogleSignInRoutes(this WebApplication app)
    {
        // Public: starts the redirect to Google for a person who is not signed in yet.
        app.MapGet("/api/auth/google/start", (GoogleSettings settings) =>
            settings.Enabled
                ? Results.Challenge(new AuthenticationProperties { RedirectUri = CompleteUrl }, [GoogleSettings.Scheme])
                : Results.Json(new { message = "Google sign-in is not set up on this server.", isError = true }, statusCode: 404))
            .AllowAnonymous();

        // Public: Google sends the person back here; the identity is read from the framework's external cookie.
        app.MapGet(CompleteUrl, CompleteAsync).AllowAnonymous();
    }

    private static async Task<IResult> CompleteAsync(
        HttpContext http, GoogleSettings settings, UserManager<AppUser> users, SignInManager<AppUser> signIn,
        RegistrationService registration, IDatabaseHelper<Guid> db, ILogger<RegistrationService> logger)
    {
        if (!settings.Enabled) return Results.Redirect(FailedRedirect);

        var external = await http.AuthenticateAsync(IdentityConstants.ExternalScheme);
        await http.SignOutAsync(IdentityConstants.ExternalScheme);

        // An email Google has not verified proves nothing about who the person is.
        var google = ReadVerifiedIdentity(external.Principal);
        if (google is null) return Results.Redirect(FailedRedirect);

        // This is a GET, so the request transaction middleware does not wrap it: open our own so a company
        // and its first Admin are created together or not at all.
        db.BeginTransaction();
        AppUser? user;
        try
        {
            user = await ResolveUserAsync(users, registration, google.Email, google.Name, google.Subject);
            db.CommitTransaction();
        }
        catch (Exception ex)
        {
            db.RollBackTransaction();
            logger.LogWarning("Google sign-in failed ({ErrorType})", ex.GetType().Name);
            return Results.Redirect(FailedRedirect);
        }

        if (user is null) return Results.Redirect(FailedRedirect);
        if (!user.IsActive) return Results.Redirect(SwitchedOffRedirect);

        await signIn.SignInAsync(user, isPersistent: true);
        return Results.Redirect("/");
    }

    internal sealed record GoogleIdentity(string Subject, string Email, string? Name);

    // Null when Google did not give a stable id and an email it has verified.
    internal static GoogleIdentity? ReadVerifiedIdentity(ClaimsPrincipal? principal)
    {
        var subject = principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        var email = principal?.FindFirstValue(ClaimTypes.Email)?.Trim();
        var verified = string.Equals(principal?.FindFirstValue("email_verified"), "true", StringComparison.OrdinalIgnoreCase);
        return string.IsNullOrEmpty(subject) || string.IsNullOrEmpty(email) || !verified
            ? null
            : new GoogleIdentity(subject, email, principal?.FindFirstValue(ClaimTypes.Name));
    }

    internal static async Task<AppUser?> ResolveUserAsync(
        UserManager<AppUser> users, RegistrationService registration, string email, string? displayName, string subject)
    {
        var linked = await users.FindByLoginAsync(GoogleSettings.Scheme, subject);
        if (linked is not null) return linked;

        var existing = await users.FindByEmailAsync(email);
        if (existing is not null)
        {
            await users.AddLoginAsync(existing, new UserLoginInfo(GoogleSettings.Scheme, subject, GoogleSettings.Scheme));
            return existing;
        }

        // Two quick sign-ins race on the unique email index; the loser finds the winner's account.
        var created = await registration.RegisterExternalAsync(email, displayName ?? email, GoogleSettings.Scheme, subject);
        return created ?? await users.FindByEmailAsync(email);
    }
}
