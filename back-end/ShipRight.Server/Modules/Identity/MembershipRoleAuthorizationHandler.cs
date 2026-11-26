using Microsoft.AspNetCore.Authorization;

namespace ShipRight.Modules.Identity;

/// <summary>
/// Authorises the role held in the company the browser has explicitly switched to. The membership was checked by
/// <see cref="ActiveCompanyMiddleware"/> for this request, so the role here is never taken from the browser.
/// </summary>
public sealed class MembershipRoleAuthorizationHandler(IHttpContextAccessor accessor)
    : AuthorizationHandler<RoleAccessRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, RoleAccessRequirement requirement)
    {
        if (!ActiveCompanyContext.IsExplicit) return Task.CompletedTask;

        var http = context.Resource as HttpContext ?? accessor.HttpContext;
        if (http is null) return Task.CompletedTask;

        var required = RoleAccessRules.RequiredRole(http.Request.Method, http.Request.Path.Value ?? "/");
        if (required is null)
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        if (context.User.Identity?.IsAuthenticated != true) return Task.CompletedTask;

        var mustChange = context.User.HasClaim(AppClaimsPrincipalFactory.MustChangePasswordClaim, "true");
        if (mustChange && !RoleAccessRules.AllowedWhilePasswordChangeIsPending(http.Request.Path.Value ?? "/"))
            return Task.CompletedTask;

        if (AppRoles.Rank(ActiveCompanyContext.Role) >= AppRoles.Rank(required)) context.Succeed(requirement);
        return Task.CompletedTask;
    }
}
