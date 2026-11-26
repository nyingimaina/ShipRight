using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using ShipRight.Shared.Tenancy;

namespace ShipRight.Modules.Identity;

/// <summary>
/// The company a signed-in person is currently working in, plus the role they hold there. Resolved from the
/// <see cref="ActiveCompanyCookie"/> after a membership check; when the cookie is absent the sign-in cookie's own
/// company and role are used exactly as before, which keeps older sessions and older builds working.
/// </summary>
public static class ActiveCompanyContext
{
    private static readonly AsyncLocal<Selection?> Current = new();

    public static Guid? CompanyId => Current.Value?.CompanyId;

    /// <summary>The role from the active membership; null when no membership cookie was resolved.</summary>
    public static string? Role => Current.Value?.Role;

    /// <summary>True when the active company was chosen explicitly (so its role must override the cookie claim).</summary>
    public static bool IsExplicit => Current.Value is not null;

    internal static void Set(Guid companyId, string role) => Current.Value = new Selection(companyId, role);

    internal static void Clear() => Current.Value = null;

    private sealed record Selection(Guid CompanyId, string Role);
}

/// <summary>Reads and writes the active-company cookie. HttpOnly: the browser never needs to read it.</summary>
public static class ActiveCompanyCookie
{
    public const string Name = "shipright.company";

    public static void Set(HttpContext http, Guid companyId) =>
        http.Response.Cookies.Append(Name, companyId.ToString(), Options(http));

    public static void Clear(HttpContext http) =>
        http.Response.Cookies.Delete(Name, Options(http));

    private static CookieOptions Options(HttpContext http) => new()
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Lax,
        Secure = http.Request.IsHttps,
        Path = "/",
        Expires = DateTimeOffset.UtcNow.AddDays(7),
    };
}

/// <summary>
/// Runs after authentication and after <see cref="CompanyContextMiddleware"/>. If the browser has chosen a company, the
/// membership is checked before it is trusted; an unknown, inactive or foreign company is ignored and the cookie is
/// cleared, leaving the person in the company carried by their sign-in cookie.
/// </summary>
public sealed class ActiveCompanyMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext http, ICompanyMembershipStore memberships)
    {
        if (http.User.Identity?.IsAuthenticated != true)
        {
            await next(http);
            return;
        }

        if (!Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
        {
            await next(http);
            return;
        }

        var all = await memberships.ListForUserAsync(userId);
        if (all.Count == 0)
        {
            // A database from before memberships: the sign-in cookie's company and role are the whole answer.
            await next(http);
            return;
        }

        CompanyMembership? membership = null;
        if (Guid.TryParse(http.Request.Cookies[ActiveCompanyCookie.Name], out var selected))
            membership = all.FirstOrDefault(m => m.CompanyId == selected);

        if (membership is null)
        {
            // No cookie, or it names a company this person can no longer enter: use their primary company when it is
            // still one of theirs, otherwise the first company they can still enter, and make the browser remember it.
            var primary = CompanyContext.FromPrincipal(http.User);
            membership = all.FirstOrDefault(m => m.CompanyId == primary) ?? all[0];
            ActiveCompanyCookie.Set(http, membership.CompanyId);
        }

        ActiveCompanyContext.Set(membership.CompanyId, membership.Role);
        CompanyContext.Set(membership.CompanyId);
        await next(http);
    }
}
