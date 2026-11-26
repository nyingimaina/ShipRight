using Microsoft.AspNetCore.Identity;

namespace ShipRight.Modules.Identity;

// Creates a person together with the thing they belong to. Hashing and normalising go through Identity so
// there is one rule for both; the rows go in through IdentityRepository, one company and one person together.
public class RegistrationService(
    IdentityRepository repository, UserManager<AppUser> userManager, ICompanyMembershipStore memberships)
{
    // A new company with this person as its Admin. Returns null when the email is already taken.
    public async Task<AppUser?> RegisterAsync(string email, string name, string? companyName, string? password)
    {
        var user = NewUser(email, name, AppRoles.Admin, mustChangePassword: false, password);
        var company = string.IsNullOrWhiteSpace(companyName) ? name : companyName.Trim();
        return await repository.TryCreateCompanyWithUserAsync(company, user) ? user : null;
    }

    // First Google sign-in: a new company named from the Google display name. No password is stored.
    public async Task<AppUser?> RegisterExternalAsync(string email, string displayName, string provider, string providerKey)
    {
        var name = string.IsNullOrWhiteSpace(displayName) ? email : displayName.Trim();
        var user = NewUser(email, name, AppRoles.Admin, mustChangePassword: false, password: null);
        if (!await repository.TryCreateCompanyWithUserAsync(name, user)) return null;
        await repository.AddLoginAsync(user.Id, provider, providerKey);
        return user;
    }

    // A teammate always lands in the calling Admin's own company; the company id is never taken from a request.
    public async Task<AppUser?> CreateTeammateAsync(Guid companyId, string email, string name, string role, string temporaryPassword)
    {
        var user = NewUser(email, name, role, mustChangePassword: true, temporaryPassword);
        user.CompanyId = companyId;
        return await repository.TryInsertUserAsync(user) ? user : null;
    }

    // ── Membership-aware versions ─────────────────────────────────────────────
    // The methods above also keep working for callers that predate memberships. These add the one row that makes the
    // person's company appear in their switcher. Each runs inside the request's transaction, so company, person and
    // membership land together or not at all.

    public async Task<AppUser?> RegisterWithMembershipAsync(string email, string name, string? companyName, string? password)
    {
        var user = await RegisterAsync(email, name, companyName, password);
        if (user is not null) await memberships.EnsureAsync(user.Id, user.CompanyId, user.Role);
        return user;
    }

    public async Task<AppUser?> RegisterExternalWithMembershipAsync(
        string email, string displayName, string provider, string providerKey)
    {
        var user = await RegisterExternalAsync(email, displayName, provider, providerKey);
        if (user is not null) await memberships.EnsureAsync(user.Id, user.CompanyId, user.Role);
        return user;
    }

    /// <summary>
    /// Adds a person to a company. A brand-new email gets an account with the temporary password; an email that
    /// already belongs to ShipRight is simply given a membership, so one sign-in works across companies and no
    /// second account (or password) is ever created.
    /// </summary>
    public async Task<AppUser?> CreateTeammateWithMembershipAsync(
        Guid companyId, string email, string name, string role, string temporaryPassword)
    {
        var user = await CreateTeammateAsync(companyId, email, name, role, temporaryPassword);
        if (user is not null)
        {
            await memberships.EnsureAsync(user.Id, companyId, role);
            return user;
        }

        var existing = await userManager.FindByEmailAsync(email);
        if (existing is not { IsActive: true }) return null;
        if (await memberships.FindAsync(existing.Id, companyId) is not null) return null;

        await memberships.EnsureAsync(existing.Id, companyId, role);
        return existing;
    }

    private AppUser NewUser(string email, string name, string role, bool mustChangePassword, string? password)
    {
        var cleanEmail = email.Trim();
        var user = new AppUser
        {
            Id = Guid.NewGuid(),
            Email = cleanEmail,
            NormalizedEmail = userManager.NormalizeEmail(cleanEmail) ?? cleanEmail.ToUpperInvariant(),
            Name = name.Trim(),
            Role = role,
            MustChangePassword = mustChangePassword,
        };
        if (password is not null) user.PasswordHash = userManager.PasswordHasher.HashPassword(user, password);
        return user;
    }
}
