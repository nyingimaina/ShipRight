using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace MyApp.Api.Features.Auth;

/// <summary>Something the person running the app has to change in its settings. The message says what, in plain words.</summary>
public sealed class AuthSetupException(string message) : Exception(message);

/// <summary>
/// How people are known to the app. By default (single) there is no sign-in: the app is for one person on their own computer, and every
/// request is that one local owner, so nothing asks for a password. The sign-in code (accounts, passwords, lockout, cookies) is all still here,
/// switched off; set AUTH_MODE=multi to use it. Every query is scoped by owner either way, so switching on sign-in needs no change to the features.
/// </summary>
public static class AuthMode
{
    public const long LocalOwnerId = 1;
    public const string LocalOwnerEmail = "local@localhost";

    /// <summary>Stored as the local owner's password: not a valid hash of anything, so nobody can sign in as them until someone takes the account over.</summary>
    public const string LocalOwnerNoPassword = "!";

    /// <summary>AUTH_MODE (environment or .env) wins over Auth:Mode (settings file). Anything but "multi" means single.</summary>
    private static string? Setting(IConfiguration config) => config["AUTH_MODE"] is { Length: > 0 } env ? env : config["Auth:Mode"];

    public static bool IsSingle(IConfiguration config) => !string.Equals(Setting(config), "multi", StringComparison.OrdinalIgnoreCase);

    /// <summary>Called at startup. A mistake in the mode is explained rather than guessed at, and a public deployment can't be left open by accident.</summary>
    public static void Validate(IConfiguration config, IHostEnvironment env)
    {
        var mode = Setting(config);
        if (!string.IsNullOrEmpty(mode) && !mode.Equals("single", StringComparison.OrdinalIgnoreCase) && !mode.Equals("multi", StringComparison.OrdinalIgnoreCase))
            throw new AuthSetupException($"AUTH_MODE is \"{mode}\". Use AUTH_MODE=single (no sign-in, one person: the default) or AUTH_MODE=multi (people sign in with their own accounts).");

        if (IsSingle(config) && env.IsProduction() && !config.GetValue<bool>("Auth:AllowSingleUserInProduction"))
            throw new AuthSetupException("""

                This app has no sign-in (single-user mode), so anyone who can reach it can read and change everything in it.
                That is fine on your own computer, but not on a server that other people can reach.

                To fix it, either:
                  - turn sign-in on:   AUTH_MODE=multi   (in your .env file, or as an environment variable), or
                  - if the app really is only reachable from this computer, say so:   Auth__AllowSingleUserInProduction=true
                """);
    }
}

/// <summary>Single-user mode: every request is the one local owner. Nothing to sign in to, nothing to forget.</summary>
internal sealed class LocalOwnerHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "LocalOwner";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, AuthMode.LocalOwnerId.ToString(System.Globalization.CultureInfo.InvariantCulture)), new Claim(ClaimTypes.Email, AuthMode.LocalOwnerEmail)],
            SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
