using Microsoft.AspNetCore.Authorization;

namespace ShipRight.Modules.Identity;

// The fallback policy: every endpoint needs sign-in and the role from RoleAccessRules, unless the endpoint
// is explicitly marked AllowAnonymous (each of those carries a reason comment in AuthEndpoints).
public sealed class RoleAccessRequirement : IAuthorizationRequirement;

public sealed class RoleAuthorizationHandler(IHttpContextAccessor accessor) : AuthorizationHandler<RoleAccessRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, RoleAccessRequirement requirement)
    {
        var http = context.Resource as HttpContext ?? accessor.HttpContext;
        if (http is null) return Task.CompletedTask;

        var required = RoleAccessRules.RequiredRole(http.Request.Method, http.Request.Path.Value ?? "/");
        if (required is null)
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        var user = context.User;
        if (user.Identity?.IsAuthenticated != true) return Task.CompletedTask;

        var mustChange = user.HasClaim(AppClaimsPrincipalFactory.MustChangePasswordClaim, "true");
        if (mustChange && !RoleAccessRules.AllowedWhilePasswordChangeIsPending(http.Request.Path.Value ?? "/"))
            return Task.CompletedTask;

        var role = user.FindFirst(global::System.Security.Claims.ClaimTypes.Role)?.Value;
        if (AppRoles.Rank(role) >= AppRoles.Rank(required)) context.Succeed(requirement);
        return Task.CompletedTask;
    }
}
