using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Jattac.Libraries.QBuilder;
using Rocket.Libraries.DatabaseIntegrator;
using ShipRight.Database;
using ShipRight.Modules.Auth.Models;
using ShipRight.Modules.Auth.Services;

namespace ShipRight.Modules.Auth;

public static class AuthRouter
{
    public static void MapAuthRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/auth");

        group.MapPost("/signup", SignUpAsync);
        group.MapPost("/login", LoginAsync);
        group.MapPost("/refresh", RefreshAsync);
        group.MapPost("/logout", LogoutAsync);
        group.MapPost("/forgot-password", ForgotPasswordAsync);
        group.MapPost("/reset-password", ResetPasswordAsync);
        group.MapPost("/revoke-all", RevokeAllSessionsAsync);
    }

    private static async Task<IResult> SignUpAsync(
        SignUpRequest request,
        IDatabaseHelper<Guid> db,
        IPasswordHelper passwordHelper,
        ITokenService tokenService)
    {
        using var checkQb = new QBuilder(parameterize: true);
        var checkBuilt = checkQb
            .UseSelector()
            .Select<User>("*")
            .Then()
            .UseTableBoundFilter<User>()
            .WhereEqualTo(u => u.Email, request.Email)
            .Then()
            .BuildWithParameters();

        var existing = await db.GetManyAsync<User>(checkBuilt.ParameterizedSql, checkBuilt.Parameters);
        if (existing.Count > 0)
            return Results.BadRequest(new { message = "Email already registered", isError = true });

        var companyId = Guid.NewGuid();
        var company = new Company
        {
            Id = companyId,
            Name = request.CompanyName,
            Slug = request.CompanyName.ToLowerInvariant().Replace(" ", "-"),
            Created = DateTime.UtcNow,
            Modified = DateTime.UtcNow,
        };

        using var companyInsert = new QBuilder(parameterize: true);
        var companyBuilt = companyInsert
            .UseTableBoundInsert<Company>()
            .FromObject(company)
            .BuildWithParameters();
        await db.ExecuteAsync(companyBuilt.ParameterizedSql, companyBuilt.Parameters);

        var user = new User
        {
            Id = Guid.NewGuid(),
            CompanyId = companyId,
            Email = request.Email,
            PasswordHash = passwordHelper.HashPassword(request.Password),
            Name = request.Name,
            IsAdmin = true,
            TokenVersion = 1,
            Created = DateTime.UtcNow,
            Modified = DateTime.UtcNow,
        };

        using var userInsert = new QBuilder(parameterize: true);
        var userBuilt = userInsert
            .UseTableBoundInsert<User>()
            .FromObject(user)
            .BuildWithParameters();
        await db.ExecuteAsync(userBuilt.ParameterizedSql, userBuilt.Parameters);

        var (accessToken, refreshToken) = await tokenService.CreateSessionAsync(user);

        return Results.Json(new
        {
            accessToken,
            refreshToken,
            user = new
            {
                id = user.Id,
                email = user.Email,
                name = user.Name,
                companyId = user.CompanyId,
                isAdmin = user.IsAdmin,
            },
        });
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest request,
        IDatabaseHelper<Guid> db,
        IPasswordHelper passwordHelper,
        ITokenService tokenService)
    {
        using var qBuilder = new QBuilder(parameterize: true);
        var built = qBuilder
            .UseSelector()
            .Select<User>("*")
            .Then()
            .UseTableBoundFilter<User>()
            .WhereEqualTo(u => u.Email, request.Email)
            .Then()
            .BuildWithParameters();

        var users = await db.GetManyAsync<User>(built.ParameterizedSql, built.Parameters);
        var user = users.FirstOrDefault();

        if (user == null || !passwordHelper.VerifyPassword(request.Password, user.PasswordHash))
        {
            return Results.Json(new { message = "Invalid email or password", isError = true }, statusCode: 401);
        }

        var (accessToken, refreshToken) = await tokenService.CreateSessionAsync(user);

        return Results.Json(new
        {
            accessToken,
            refreshToken,
            user = new
            {
                id = user.Id,
                email = user.Email,
                name = user.Name,
                companyId = user.CompanyId,
                isAdmin = user.IsAdmin,
            },
        });
    }

    private static async Task<IResult> RefreshAsync(
        HttpContext context,
        IDatabaseHelper<Guid> db,
        ITokenService tokenService,
        IJwtService jwtService)
    {
        var refreshToken = context.Request.Headers["X-Refresh-Token"].FirstOrDefault();
        if (string.IsNullOrEmpty(refreshToken))
        {
            return Results.Json(new { message = "Refresh token required", isError = true }, statusCode: 401);
        }

        var result = await tokenService.RefreshSessionAsync(refreshToken);
        if (result == null)
        {
            return Results.Json(new { message = "Invalid or expired refresh token", isError = true }, statusCode: 401);
        }

        return Results.Json(new { accessToken = result.Value.accessToken, refreshToken = result.Value.refreshToken });
    }

    private static async Task<IResult> LogoutAsync(
        HttpContext context,
        ITokenService tokenService)
    {
        var refreshToken = context.Request.Headers["X-Refresh-Token"].FirstOrDefault();
        if (!string.IsNullOrEmpty(refreshToken))
        {
            await tokenService.RevokeRefreshTokenAsync(refreshToken);
        }

        return Results.Ok(new { message = "Logged out" });
    }

    private static async Task<IResult> ForgotPasswordAsync(
        ForgotPasswordRequest request,
        IDatabaseHelper<Guid> db)
    {
        using var qBuilder = new QBuilder(parameterize: true);
        var built = qBuilder
            .UseSelector()
            .Select<User>("*")
            .Then()
            .UseTableBoundFilter<User>()
            .WhereEqualTo(u => u.Email, request.Email)
            .Then()
            .BuildWithParameters();

        var users = await db.GetManyAsync<User>(built.ParameterizedSql, built.Parameters);
        var user = users.FirstOrDefault();

        if (user == null)
            return Results.Ok(new { message = "If the email exists, a reset link has been generated" });

        var tokenBytes = new byte[32];
        RandomNumberGenerator.Fill(tokenBytes);
        var token = Convert.ToBase64String(tokenBytes)
            .Replace("/", "_").Replace("+", "-").Replace("=", "");
        var tokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

        var resetToken = new ResetToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TokenHash = tokenHash,
            ExpiresAt = DateTime.UtcNow.AddHours(1),
            Created = DateTime.UtcNow,
            Modified = DateTime.UtcNow,
        };

        using var insertQb = new QBuilder(parameterize: true);
        var insertBuilt = insertQb
            .UseTableBoundInsert<ResetToken>()
            .FromObject(resetToken)
            .BuildWithParameters();
        await db.ExecuteAsync(insertBuilt.ParameterizedSql, insertBuilt.Parameters);

        return Results.Ok(new { message = "If the email exists, a reset link has been generated", resetToken = token });
    }

    private static async Task<IResult> ResetPasswordAsync(
        ResetPasswordRequest request,
        IDatabaseHelper<Guid> db,
        IPasswordHelper passwordHelper,
        ITokenService tokenService)
    {
        var tokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.Token))).ToLowerInvariant();

        using var qBuilder = new QBuilder(parameterize: true);
        var built = qBuilder
            .UseSelector()
            .Select<ResetToken>("*")
            .Then()
            .UseTableBoundFilter<ResetToken>()
            .WhereEqualTo(t => t.TokenHash, tokenHash)
            .Then()
            .BuildWithParameters();

        var tokens = await db.GetManyAsync<ResetToken>(built.ParameterizedSql, built.Parameters);
        var stored = tokens.FirstOrDefault();

        if (stored == null || stored.UsedAt != null || stored.ExpiresAt < DateTime.UtcNow)
            return Results.BadRequest(new { message = "Invalid or expired reset token", isError = true });

        using var userQb = new QBuilder(parameterize: true);
        var userBuilt = userQb
            .UseSelector()
            .Select<User>("*")
            .Then()
            .UseTableBoundFilter<User>()
            .WhereEqualTo(u => u.Id, stored.UserId)
            .Then()
            .BuildWithParameters();

        var users = await db.GetManyAsync<User>(userBuilt.ParameterizedSql, userBuilt.Parameters);
        var user = users.FirstOrDefault();

        if (user == null)
            return Results.BadRequest(new { message = "User not found", isError = true });

        using var updateUserQb = new QBuilder(parameterize: true);
        var updateUserBuilt = updateUserQb
            .UseTableBoundUpdate<User>()
            .Set(u => u.PasswordHash, passwordHelper.HashPassword(request.NewPassword))
            .Set(u => u.TokenVersion, user.TokenVersion + 1)
            .Set(u => u.Modified, DateTime.UtcNow)
            .WhereEqualTo(u => u.Id, user.Id)
            .BuildWithParameters();
        await db.ExecuteAsync(updateUserBuilt.ParameterizedSql, updateUserBuilt.Parameters);

        using var useTokenQb = new QBuilder(parameterize: true);
        var useTokenBuilt = useTokenQb
            .UseTableBoundUpdate<ResetToken>()
            .Set(t => t.UsedAt, DateTime.UtcNow)
            .WhereEqualTo(t => t.Id, stored.Id)
            .BuildWithParameters();
        await db.ExecuteAsync(useTokenBuilt.ParameterizedSql, useTokenBuilt.Parameters);

        await tokenService.RevokeAllUserSessionsAsync(user.Id);

        return Results.Ok(new { message = "Password reset successfully" });
    }

    private static async Task<IResult> RevokeAllSessionsAsync(
        HttpContext context,
        ITokenService tokenService)
    {
        if (context.Items["UserId"] is not Guid userId)
            return Results.Unauthorized();

        await tokenService.RevokeAllUserSessionsAsync(userId);
        return Results.Ok(new { message = "All sessions revoked" });
    }
}

public record SignUpRequest(string Email, string Password, string Name, string CompanyName);
public record LoginRequest(string Email, string Password);
public record ForgotPasswordRequest(string Email);
public record ResetPasswordRequest(string Token, string NewPassword);
