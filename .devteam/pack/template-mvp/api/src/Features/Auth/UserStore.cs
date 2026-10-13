using MyApp.Api.Shared.Storage;

namespace MyApp.Api.Features.Auth;

/// <summary>A user as stored. Never sent to the browser: the HTTP layer returns <see cref="UserDto"/> instead (API-004).</summary>
internal sealed record UserRow(long Id, string Email, string PasswordHash, int FailedLogins, DateTime? LockedUntil);

/// <summary>What is saved in users.json.</summary>
public sealed class StoredUser
{
    public long Id { get; set; }
    public string Email { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public int FailedLogins { get; set; }
    public DateTime? LockedUntil { get; set; }
}

/// <summary>
/// All storage for users, in a file (fast-MVP version). The methods are the same ones the database version has, so the HTTP code
/// does not change when the database is added later.
/// </summary>
internal sealed class UserStore(FileStorage files)
{
    private readonly FileTable<StoredUser> _users = files.Table<StoredUser>("users");

    public Task<UserRow?> FindByEmailAsync(string email, CancellationToken ct) =>
        Task.FromResult(_users.Read(all => all.Where(u => Same(u.Email, email)).Select(ToRow).FirstOrDefault()));

    /// <summary>Single-user mode: makes sure the local owner (id 1) exists. Refuses to mix them in with people who really signed up.</summary>
    public Task EnsureLocalOwnerAsync(CancellationToken ct) =>
        Task.FromResult(_users.Write(table =>
        {
            var existing = table.Items.FirstOrDefault(u => u.Id == AuthMode.LocalOwnerId);
            if (existing is null)
            {
                if (table.NextId <= AuthMode.LocalOwnerId) table.NextId = AuthMode.LocalOwnerId + 1;
                table.Items.Add(new StoredUser { Id = AuthMode.LocalOwnerId, Email = AuthMode.LocalOwnerEmail, PasswordHash = AuthMode.LocalOwnerNoPassword });
            }
            else if (existing.PasswordHash != AuthMode.LocalOwnerNoPassword && existing.Email != AuthMode.LocalOwnerEmail)
                throw new AuthSetupException("This app's data already has people who signed up, so it can't be used with no sign-in without mixing their data in. " +
                                             "Set AUTH_MODE=multi to keep using sign-in, or use a new data folder.");
            return true;
        }));

    /// <summary>
    /// The first registration after single-user mode takes over the local owner (id 1): same id, so their records come with the account.
    /// Returns the id, or null when there is nothing to take over (or that email is already taken).
    /// </summary>
    public Task<long?> ClaimLocalOwnerAsync(string email, string passwordHash, CancellationToken ct) =>
        Task.FromResult(_users.Write(table =>
        {
            if (table.Items.FirstOrDefault(u => u.Id == AuthMode.LocalOwnerId && u.PasswordHash == AuthMode.LocalOwnerNoPassword) is not { } owner) return (long?)null;
            if (table.Items.Any(u => u.Id != owner.Id && Same(u.Email, email))) return null;
            owner.Email = email;
            owner.PasswordHash = passwordHash;
            return owner.Id;
        }));

    /// <summary>Returns the new id, or null if the email is already registered (checked and saved together, so two requests can't both win).</summary>
    public Task<long?> CreateAsync(string email, string passwordHash, CancellationToken ct) =>
        Task.FromResult(_users.Write(table =>
        {
            if (table.Items.Any(u => Same(u.Email, email))) return (long?)null;
            var id = table.TakeId();
            table.Items.Add(new StoredUser { Id = id, Email = email, PasswordHash = passwordHash });
            return id;
        }));

    /// <summary>Counts a failed sign-in; after <paramref name="lockAfter"/> failures the account is locked until <paramref name="lockUntil"/>.</summary>
    public Task RecordFailureAsync(long id, int lockAfter, DateTime lockUntil, CancellationToken ct) =>
        Task.FromResult(_users.Write(table =>
        {
            if (table.Items.FirstOrDefault(u => u.Id == id) is not { } user) return false;
            user.FailedLogins++;
            if (user.FailedLogins >= lockAfter) user.LockedUntil = lockUntil;
            return true;
        }));

    public Task ResetFailuresAsync(long id, CancellationToken ct) =>
        Task.FromResult(_users.Write(table =>
        {
            if (table.Items.FirstOrDefault(u => u.Id == id) is not { } user) return false;
            user.FailedLogins = 0;
            user.LockedUntil = null;
            return true;
        }));

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static UserRow ToRow(StoredUser u) => new(u.Id, u.Email, u.PasswordHash, u.FailedLogins, u.LockedUntil);
}
