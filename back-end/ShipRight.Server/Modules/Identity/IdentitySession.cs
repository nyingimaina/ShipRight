using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using ShipRight.Shared.Time;

namespace ShipRight.Modules.Identity;

/// <summary>
/// Builds the session the browser sees. The active company is always one the person is an active member of; when
/// memberships do not exist yet (an old database before migration) the person's primary company is used, which is
/// exactly what older builds returned.
/// </summary>
public static class IdentitySession
{
    public static async Task<SessionDto> BuildAsync(
        AppUser user, IdentityRepository identity, ICompanyMembershipStore memberships,
        Guid? activeCompanyId = null, string? activeRole = null)
    {
        var all = await memberships.ListForUserAsync(user.Id);

        if (all.Count == 0)
        {
            var id = activeCompanyId ?? user.CompanyId;
            var role = activeRole ?? user.Role;
            var name = await identity.GetCompanyNameAsync(id);
            return new SessionDto(new SessionUserDto(
                user.Id, user.Email, user.Name, id, role, user.MustChangePassword, name,
                [new CompanyChoiceDto(id, name, role, true, true)],
                AppTimeZone.For(user.TimeZone)));
        }

        var active = (activeCompanyId is { } chosen ? all.FirstOrDefault(m => m.CompanyId == chosen) : null)
                     ?? all.FirstOrDefault(m => m.CompanyId == user.CompanyId)
                     ?? all[0];

        return new SessionDto(new SessionUserDto(
            user.Id, user.Email, user.Name, active.CompanyId, active.Role, user.MustChangePassword, active.CompanyName,
            all.Select(m => new CompanyChoiceDto(
                m.CompanyId, m.CompanyName, m.Role, m.IsActive, m.CompanyId == active.CompanyId)).ToList(),
            AppTimeZone.For(user.TimeZone)));
    }

    /// <summary>
    /// After a fresh sign-in: keep the company this browser last used when it is still one of theirs, otherwise land
    /// on their primary company (or the first one they can still enter).
    /// </summary>
    public static async Task<(Guid CompanyId, string Role)> ResolveForSignInAsync(
        HttpContext http, AppUser user, ICompanyMembershipStore memberships)
    {
        var all = await memberships.ListForUserAsync(user.Id);
        if (all.Count == 0) return (user.CompanyId, user.Role);

        if (Guid.TryParse(http.Request.Cookies[ActiveCompanyCookie.Name], out var remembered)
            && all.FirstOrDefault(m => m.CompanyId == remembered) is { } previous)
            return (previous.CompanyId, previous.Role);

        var preferred = all.FirstOrDefault(m => m.CompanyId == user.CompanyId) ?? all[0];
        return (preferred.CompanyId, preferred.Role);
    }

    /// <summary>Switches the browser to a company it has already proved it may enter.</summary>
    public static void Select(HttpContext http, Guid companyId, string role)
    {
        ActiveCompanyContext.Set(companyId, role);
        Shared.Tenancy.CompanyContext.Set(companyId);
        ActiveCompanyCookie.Set(http, companyId);
    }

    /// <summary>The active choice made by <see cref="ActiveCompanyMiddleware"/>, if this request had one.</summary>
    public static (Guid? CompanyId, string? Role) ExplicitChoice =>
        ActiveCompanyContext.IsExplicit ? (ActiveCompanyContext.CompanyId, ActiveCompanyContext.Role) : (null, null);
}
