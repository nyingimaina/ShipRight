using Microsoft.Extensions.Logging;

namespace ShipRight.Modules.Identity;

// Temporary tracing for sign-in problems. Logs cookie NAMES only, never values or tokens.
public sealed class AuthDiagnosticsMiddleware(RequestDelegate next, ILogger<AuthDiagnosticsMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        if (!path.StartsWith("/api/auth/", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        var cookies = string.Join(",", context.Request.Cookies.Keys);
        var request = context.Request;
        logger.LogInformation(
            "[AUTH-DIAG] -> {Method} {Path} scheme={Scheme} host={Host} fwdProto={FwdProto} fwdHost={FwdHost} cookies=[{Cookies}] authenticated={Authenticated}",
            request.Method, path, request.Scheme, request.Host,
            request.Headers["X-Forwarded-Proto"].ToString(), request.Headers["X-Forwarded-Host"].ToString(),
            cookies, context.User.Identity?.IsAuthenticated);

        await next(context);

        var setCookies = string.Join(",", context.Response.Headers.SetCookie
            .SelectMany(v => (v ?? string.Empty).Split(';'))
            .Select(v => v.Split('=')[0].Trim())
            .Where(n => n.Length > 0));
        logger.LogInformation(
            "[AUTH-DIAG] <- {Method} {Path} status={Status} setCookies=[{SetCookies}] authenticated={Authenticated}",
            request.Method, path, context.Response.StatusCode, setCookies, context.User.Identity?.IsAuthenticated);
    }
}
