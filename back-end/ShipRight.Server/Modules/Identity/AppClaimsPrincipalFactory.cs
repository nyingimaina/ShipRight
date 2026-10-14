using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using ShipRight.Shared.Tenancy;

namespace ShipRight.Modules.Identity;

// Puts the company and the one role into the sign-in cookie. The cookie is re-issued from the database
// by Identity's security-stamp check every minute, so a changed role applies within a minute (R5).
public sealed class AppClaimsPrincipalFactory(UserManager<AppUser> userManager, IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<AppUser>(userManager, options)
{
    public const string MustChangePasswordClaim = "must_change_password";
    public const string DisplayNameClaim = "display_name";

    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(AppUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        identity.AddClaim(new Claim(AppClaims.CompanyId, user.CompanyId.ToString()));
        identity.AddClaim(new Claim(ClaimTypes.Role, user.Role));
        identity.AddClaim(new Claim(ClaimTypes.Email, user.Email));
        identity.AddClaim(new Claim(DisplayNameClaim, user.Name));
        if (user.MustChangePassword) identity.AddClaim(new Claim(MustChangePasswordClaim, "true"));
        return identity;
    }
}
