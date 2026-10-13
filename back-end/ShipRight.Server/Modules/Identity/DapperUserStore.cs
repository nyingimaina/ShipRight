using Microsoft.AspNetCore.Identity;

namespace ShipRight.Modules.Identity;

// Minimal Identity user store on the project's own SQL access (no EF Core). Role is one column per person,
// so there is no role table and no RoleManager: IUserRoleStore simply exposes that column.
public sealed class DapperUserStore(IdentityRepository repository) :
    IUserPasswordStore<AppUser>,
    IUserEmailStore<AppUser>,
    IUserRoleStore<AppUser>,
    IUserLockoutStore<AppUser>,
    IUserSecurityStampStore<AppUser>,
    IUserLoginStore<AppUser>
{
    public void Dispose() { }

    // ── IUserStore ───────────────────────────────────────────────────────────
    public Task<string> GetUserIdAsync(AppUser user, CancellationToken ct) => Task.FromResult(user.Id.ToString());
    public Task<string?> GetUserNameAsync(AppUser user, CancellationToken ct) => Task.FromResult<string?>(user.Email);
    public Task SetUserNameAsync(AppUser user, string? userName, CancellationToken ct)
    {
        user.Email = userName ?? "";
        return Task.CompletedTask;
    }
    public Task<string?> GetNormalizedUserNameAsync(AppUser user, CancellationToken ct) => Task.FromResult<string?>(user.NormalizedEmail);
    public Task SetNormalizedUserNameAsync(AppUser user, string? normalizedName, CancellationToken ct)
    {
        user.NormalizedEmail = normalizedName ?? "";
        return Task.CompletedTask;
    }

    public async Task<IdentityResult> CreateAsync(AppUser user, CancellationToken ct)
    {
        user.Created = user.Modified = DateTime.UtcNow;
        return await repository.TryInsertUserAsync(user)
            ? IdentityResult.Success
            : IdentityResult.Failed(new IdentityErrorDescriber().DuplicateEmail(user.Email));
    }

    public async Task<IdentityResult> UpdateAsync(AppUser user, CancellationToken ct)
    {
        await repository.UpdateCredentialsAsync(user);
        return IdentityResult.Success;
    }

    // People are switched off, never deleted.
    public Task<IdentityResult> DeleteAsync(AppUser user, CancellationToken ct) =>
        Task.FromResult(IdentityResult.Failed(new IdentityError { Code = "NotSupported", Description = "Accounts are switched off, not deleted." }));

    public Task<AppUser?> FindByIdAsync(string userId, CancellationToken ct) =>
        Guid.TryParse(userId, out var id) ? repository.FindByIdAsync(id) : Task.FromResult<AppUser?>(null);

    public Task<AppUser?> FindByNameAsync(string normalizedUserName, CancellationToken ct) =>
        repository.FindByNormalizedEmailAsync(normalizedUserName);

    // ── Password ─────────────────────────────────────────────────────────────
    public Task SetPasswordHashAsync(AppUser user, string? passwordHash, CancellationToken ct)
    {
        user.PasswordHash = passwordHash;
        return Task.CompletedTask;
    }
    public Task<string?> GetPasswordHashAsync(AppUser user, CancellationToken ct) => Task.FromResult(user.PasswordHash);
    public Task<bool> HasPasswordAsync(AppUser user, CancellationToken ct) => Task.FromResult(!string.IsNullOrEmpty(user.PasswordHash));

    // ── Email ────────────────────────────────────────────────────────────────
    public Task SetEmailAsync(AppUser user, string? email, CancellationToken ct)
    {
        user.Email = email ?? "";
        return Task.CompletedTask;
    }
    public Task<string?> GetEmailAsync(AppUser user, CancellationToken ct) => Task.FromResult<string?>(user.Email);
    public Task<bool> GetEmailConfirmedAsync(AppUser user, CancellationToken ct) => Task.FromResult(true);
    public Task SetEmailConfirmedAsync(AppUser user, bool confirmed, CancellationToken ct) => Task.CompletedTask;
    public Task<AppUser?> FindByEmailAsync(string normalizedEmail, CancellationToken ct) =>
        repository.FindByNormalizedEmailAsync(normalizedEmail);
    public Task<string?> GetNormalizedEmailAsync(AppUser user, CancellationToken ct) => Task.FromResult<string?>(user.NormalizedEmail);
    public Task SetNormalizedEmailAsync(AppUser user, string? normalizedEmail, CancellationToken ct)
    {
        user.NormalizedEmail = normalizedEmail ?? "";
        return Task.CompletedTask;
    }

    // ── Roles (one per person, stored on the user) ───────────────────────────
    public Task AddToRoleAsync(AppUser user, string roleName, CancellationToken ct)
    {
        user.Role = AppRoles.All.FirstOrDefault(r => string.Equals(r, roleName, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException("Unknown role.", nameof(roleName));
        return Task.CompletedTask;
    }
    public Task RemoveFromRoleAsync(AppUser user, string roleName, CancellationToken ct) => Task.CompletedTask;
    public Task<IList<string>> GetRolesAsync(AppUser user, CancellationToken ct) => Task.FromResult<IList<string>>([user.Role]);
    public Task<bool> IsInRoleAsync(AppUser user, string roleName, CancellationToken ct) =>
        Task.FromResult(string.Equals(user.Role, roleName, StringComparison.OrdinalIgnoreCase));
    public Task<IList<AppUser>> GetUsersInRoleAsync(string roleName, CancellationToken ct) =>
        Task.FromResult<IList<AppUser>>([]); // never listed across companies; the team endpoints list per company

    // ── Lockout ──────────────────────────────────────────────────────────────
    public Task<DateTimeOffset?> GetLockoutEndDateAsync(AppUser user, CancellationToken ct) =>
        Task.FromResult<DateTimeOffset?>(user.LockoutEnd is { } end ? new DateTimeOffset(DateTime.SpecifyKind(end, DateTimeKind.Utc)) : null);
    public Task SetLockoutEndDateAsync(AppUser user, DateTimeOffset? lockoutEnd, CancellationToken ct)
    {
        user.LockoutEnd = lockoutEnd?.UtcDateTime;
        return Task.CompletedTask;
    }
    public Task<int> IncrementAccessFailedCountAsync(AppUser user, CancellationToken ct) => Task.FromResult(++user.AccessFailedCount);
    public Task ResetAccessFailedCountAsync(AppUser user, CancellationToken ct)
    {
        user.AccessFailedCount = 0;
        return Task.CompletedTask;
    }
    public Task<int> GetAccessFailedCountAsync(AppUser user, CancellationToken ct) => Task.FromResult(user.AccessFailedCount);
    public Task<bool> GetLockoutEnabledAsync(AppUser user, CancellationToken ct) => Task.FromResult(true);
    public Task SetLockoutEnabledAsync(AppUser user, bool enabled, CancellationToken ct) => Task.CompletedTask;

    // ── Security stamp ───────────────────────────────────────────────────────
    public Task SetSecurityStampAsync(AppUser user, string stamp, CancellationToken ct)
    {
        user.SecurityStamp = stamp;
        return Task.CompletedTask;
    }
    public Task<string?> GetSecurityStampAsync(AppUser user, CancellationToken ct) => Task.FromResult<string?>(user.SecurityStamp);

    // ── External logins (Google) ─────────────────────────────────────────────
    public Task AddLoginAsync(AppUser user, UserLoginInfo login, CancellationToken ct) =>
        repository.AddLoginAsync(user.Id, login.LoginProvider, login.ProviderKey);
    public Task RemoveLoginAsync(AppUser user, string loginProvider, string providerKey, CancellationToken ct) =>
        repository.RemoveLoginAsync(user.Id, loginProvider, providerKey);
    public async Task<IList<UserLoginInfo>> GetLoginsAsync(AppUser user, CancellationToken ct) =>
        (await repository.GetLoginsAsync(user.Id))
            .Select(l => new UserLoginInfo(l.LoginProvider, l.ProviderKey, l.ProviderDisplayName))
            .ToList();
    public Task<AppUser?> FindByLoginAsync(string loginProvider, string providerKey, CancellationToken ct) =>
        repository.FindByLoginAsync(loginProvider, providerKey);
}
