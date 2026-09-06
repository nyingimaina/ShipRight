using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Jattac.Libraries.QBuilder;
using Rocket.Libraries.DatabaseIntegrator;
using Serilog;
using ShipRight.Database;
using ShipRight.Modules.Auth.Models;
using ShipRight.Modules.Auth.Services;

namespace ShipRight.Modules.Auth.Middleware;

public class JwtMiddleware
{
    private static readonly string[] AnonymousPaths =
    [
        "/api/auth/login",
        "/api/auth/signup",
        "/api/auth/refresh",
        "/api/auth/forgot-password",
        "/api/auth/reset-password",
        "/api/health",
    ];

    private readonly RequestDelegate _next;

    public JwtMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, IJwtService jwtService, IDatabaseHelper<Guid> db)
    {
        var path = context.Request.Path.Value ?? "";

        if (IsAnonymousPath(path))
        {
            await _next(context);
            return;
        }

        if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) == false)
        {
            await _next(context);
            return;
        }

        var authHeader = context.Request.Headers.Authorization.FirstOrDefault();

        if (authHeader == null || !authHeader.StartsWith("Bearer "))
        {
            context.Response.StatusCode = 401;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"message\":\"Missing or invalid Authorization header\",\"isError\":true}");
            return;
        }

        var token = authHeader["Bearer ".Length..].Trim();
        var principal = jwtService.ValidateToken(token);

        if (principal == null)
        {
            context.Response.StatusCode = 401;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"message\":\"Invalid or expired token\",\"isError\":true}");
            return;
        }

        var userIdClaim = principal.FindFirst(ClaimTypes.NameIdentifier) ?? principal.FindFirst(JwtRegisteredClaimNames.Sub);
        var emailClaim = principal.FindFirst(JwtRegisteredClaimNames.Email);
        var companyIdClaim = principal.FindFirst("company_id");
        var tokenVersionClaim = principal.FindFirst("token_version");
        var isAdminClaim = principal.FindFirst("is_admin");

        if (userIdClaim == null || companyIdClaim == null)
        {
            context.Response.StatusCode = 401;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"message\":\"Invalid token claims\",\"isError\":true}");
            return;
        }

        var userId = Guid.Parse(userIdClaim.Value);
        var companyId = Guid.Parse(companyIdClaim.Value);
        var tokenVersion = int.Parse(tokenVersionClaim?.Value ?? "0");

        using var qBuilder = AppQBuilderExtensions.NewQBuilder();
        var built = qBuilder
            .UseSelector()
            .Select<User>("TokenVersion")
            .Then()
            .UseTableBoundFilter<User>()
            .WhereEqualTo(u => u.Id, userId)
            .Then()
            .BuildWithParameters();

        var users = await db.GetManyAsync<User>(built.ParameterizedSql, built.Parameters);
        var user = users.FirstOrDefault();

        if (user == null || user.TokenVersion > tokenVersion)
        {
            context.Response.StatusCode = 401;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"message\":\"Token has been revoked\",\"isError\":true}");
            return;
        }

        context.Items["UserId"] = userId;
        context.Items["CompanyId"] = companyId;
        context.Items["Email"] = emailClaim?.Value ?? "";
        context.Items["IsAdmin"] = bool.Parse(isAdminClaim?.Value ?? "false");

        await _next(context);
    }

    internal static bool IsAnonymousPath(string path) =>
        AnonymousPaths.Contains(path.TrimEnd('/'), StringComparer.OrdinalIgnoreCase);
}
