using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using ShipRight.Modules.Auth.Services;

namespace ShipRight.Modules.Identity;

public static class AuthEndpoints
{
    public const string PasswordResetLimiter = "PasswordReset";
    public const string AuthAttemptsLimiter = "AuthAttempts";

    internal const string InvalidLogin = "Invalid email or password.";
    internal const string LockedOut = "Too many sign-in attempts. Try again in a few minutes.";
    internal const string SwitchedOff = "Your account is switched off. Contact your Admin.";
    internal const string ForgotPasswordReply = "If the email exists, a reset link has been sent.";
    internal const string InvalidResetLink = "This reset link is invalid or has expired. Ask for a new one.";

    public static void MapIdentityAuthRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/auth");

        // Public: this is how a person signs in, so no sign-in can exist yet.
        group.MapPost("/login", LoginAsync).AllowAnonymous().RequireRateLimiting(AuthAttemptsLimiter);
        // Public: registration creates the person's company and Admin account; nobody is signed in yet.
        group.MapPost("/register", RegisterAsync).AllowAnonymous().RequireRateLimiting(AuthAttemptsLimiter);
        // Public: lets the sign-in screen know whether to show the Google button.
        group.MapGet("/providers", (GoogleSettings google) => Results.Ok(new ProvidersDto(google.Enabled))).AllowAnonymous();
        // Public: the person has forgotten their password so cannot be signed in. Same reply whether or not the email exists.
        group.MapPost("/forgot-password", ForgotPasswordAsync).AllowAnonymous().RequireRateLimiting(PasswordResetLimiter);
        // Public: reached from the emailed link; protected by the single-use reset token.
        group.MapPost("/reset-password", ResetPasswordAsync).AllowAnonymous().RequireRateLimiting(AuthAttemptsLimiter);

        group.MapPost("/logout", LogoutAsync);
        group.MapGet("/me", MeAsync);
        group.MapPost("/change-password", ChangePasswordAsync);

        app.MapGoogleSignInRoutes();
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest request, UserManager<AppUser> users, SignInManager<AppUser> signIn)
    {
        var email = IdentityValidation.Trimmed(request.Email);
        if (email.Length == 0 || string.IsNullOrEmpty(request.Password))
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                [email.Length == 0 ? "email" : "password"] = ["Enter your email and password."],
            });

        var user = await users.FindByEmailAsync(email);
        if (user is null) return Unauthorized(InvalidLogin);

        var result = await signIn.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: true);
        if (result.IsLockedOut) return Results.Json(new { message = LockedOut, isError = true }, statusCode: 429);
        if (!result.Succeeded) return Unauthorized(InvalidLogin);
        if (!user.IsActive) return Results.Json(new { message = SwitchedOff, isError = true }, statusCode: 403);

        await signIn.SignInAsync(user, isPersistent: true);
        return Results.Ok(ToSession(user));
    }

    private static async Task<IResult> RegisterAsync(
        RegisterRequest request, RegistrationService registration, SignInManager<AppUser> signIn)
    {
        var errors = IdentityValidation.Register(request);
        if (errors.Count > 0) return Results.ValidationProblem(errors);

        var user = await registration.RegisterAsync(
            request.Email!, request.Name!, request.CompanyName, request.Password);
        if (user is null)
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["email"] = ["That email is already registered. Sign in instead."],
            });

        await signIn.SignInAsync(user, isPersistent: true);
        return Results.Ok(ToSession(user));
    }

    private static async Task<IResult> ForgotPasswordAsync(
        ForgotPasswordRequest request, UserManager<AppUser> users, IPasswordResetEmailSender sender,
        ILogger<IdentityRepository> logger, CancellationToken ct)
    {
        var email = IdentityValidation.Trimmed(request.Email);
        var user = email.Length == 0 ? null : await users.FindByEmailAsync(email);

        // A switched-off person gets no email, and the reply never reveals which case applied.
        if (user is { IsActive: true })
        {
            var token = await users.GeneratePasswordResetTokenAsync(user);
            try
            {
                await sender.SendAsync(user.Email, $"{user.Id:N}.{token}", ct);
            }
            catch (Exception ex)
            {
                // Logged by type only: the message of a mail failure can contain the recipient or the link.
                logger.LogWarning("Password reset email could not be sent ({ErrorType})", ex.GetType().Name);
            }
        }

        return Results.Ok(new { message = ForgotPasswordReply });
    }

    private static async Task<IResult> ResetPasswordAsync(ResetPasswordRequest request, UserManager<AppUser> users)
    {
        var errors = IdentityValidation.NewPassword("newPassword", request.NewPassword);
        if (errors.Count > 0) return Results.ValidationProblem(errors);

        var parts = (request.Token ?? "").Split('.', 2);
        if (parts.Length != 2 || !Guid.TryParseExact(parts[0], "N", out var userId)) return InvalidLink();

        var user = await users.FindByIdAsync(userId.ToString());
        // Resetting the password never switches a switched-off account back on.
        if (user is not { IsActive: true }) return InvalidLink();

        var result = await users.ResetPasswordAsync(user, parts[1], request.NewPassword!);
        if (!result.Succeeded) return InvalidLink();

        user.MustChangePassword = false;
        await users.SetLockoutEndDateAsync(user, null);
        await users.ResetAccessFailedCountAsync(user);
        await users.UpdateAsync(user);
        return Results.Ok(new { message = "Your password has been reset. You can sign in now." });
    }

    private static async Task<IResult> LogoutAsync(SignInManager<AppUser> signIn)
    {
        await signIn.SignOutAsync();
        return Results.Ok(new { message = "Signed out." });
    }

    private static async Task<IResult> MeAsync(ClaimsPrincipal principal, UserManager<AppUser> users)
    {
        var user = await users.GetUserAsync(principal);
        return user is { IsActive: true } ? Results.Ok(ToSession(user)) : Unauthorized("Sign in required.");
    }

    private static async Task<IResult> ChangePasswordAsync(
        ChangePasswordRequest request, ClaimsPrincipal principal, UserManager<AppUser> users, SignInManager<AppUser> signIn)
    {
        var errors = IdentityValidation.NewPassword("newPassword", request.NewPassword);
        if (string.IsNullOrEmpty(request.CurrentPassword)) errors["currentPassword"] = ["Enter your current password."];
        if (errors.Count > 0) return Results.ValidationProblem(errors);

        var user = await users.GetUserAsync(principal);
        if (user is not { IsActive: true }) return Unauthorized("Sign in required.");

        var result = await users.ChangePasswordAsync(user, request.CurrentPassword!, request.NewPassword!);
        if (!result.Succeeded)
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["currentPassword"] = ["Your current password is not right."],
            });

        user.MustChangePassword = false;
        await users.UpdateAsync(user);
        await signIn.RefreshSignInAsync(user);
        return Results.Ok(ToSession(user));
    }

    internal static SessionDto ToSession(AppUser user) =>
        new(new SessionUserDto(user.Id, user.Email, user.Name, user.CompanyId, user.Role, user.MustChangePassword));

    private static IResult Unauthorized(string message) =>
        Results.Json(new { message, isError = true }, statusCode: 401);

    private static IResult InvalidLink() =>
        Results.Json(new { message = InvalidResetLink, isError = true }, statusCode: 400);
}
