using MySql.Data.MySqlClient;
using Rocket.Libraries.DatabaseIntegrator;

namespace ShipRight.Modules.Identity;

// All SQL for people and companies. Every statement is a constant; every value is a parameter.
public class IdentityRepository(IDatabaseHelper<Guid> db)
{
    private const int DuplicateKey = 1062;

    private const string UserColumns =
        "Id, CompanyId, Email, NormalizedEmail, Name, PasswordHash, Role, IsActive, MustChangePassword, " +
        "SecurityStamp, LockoutEnd, AccessFailedCount, Created, Modified";

    private const string SelectById =
        "SELECT " + UserColumns + " FROM `User` WHERE Id = @Id AND Deleted = 0";

    private const string SelectByEmail =
        "SELECT " + UserColumns + " FROM `User` WHERE NormalizedEmail = @NormalizedEmail AND Deleted = 0";

    private const string SelectByLogin =
        "SELECT u.Id, u.CompanyId, u.Email, u.NormalizedEmail, u.Name, u.PasswordHash, u.Role, u.IsActive, " +
        "u.MustChangePassword, u.SecurityStamp, u.LockoutEnd, u.AccessFailedCount, u.Created, u.Modified " +
        "FROM `User` u INNER JOIN ExternalLogin l ON l.UserId = u.Id " +
        "WHERE l.Provider = @Provider AND l.ProviderKey = @ProviderKey AND u.Deleted = 0";

    private const string InsertUser =
        "INSERT INTO `User` (Id, CompanyId, Email, NormalizedEmail, Name, PasswordHash, Role, IsActive, " +
        "MustChangePassword, SecurityStamp, LockoutEnd, AccessFailedCount, Created, Modified) " +
        "VALUES (@Id, @CompanyId, @Email, @NormalizedEmail, @Name, @PasswordHash, @Role, @IsActive, " +
        "@MustChangePassword, @SecurityStamp, @LockoutEnd, @AccessFailedCount, @Created, @Modified)";

    // Role, IsActive and CompanyId are deliberately absent: only the team endpoints change them,
    // so a sign-in that saves a failed-attempt counter can never overwrite an Admin's decision.
    private const string UpdateUserCredentials =
        "UPDATE `User` SET Email = @Email, NormalizedEmail = @NormalizedEmail, Name = @Name, " +
        "PasswordHash = @PasswordHash, MustChangePassword = @MustChangePassword, SecurityStamp = @SecurityStamp, " +
        "LockoutEnd = @LockoutEnd, AccessFailedCount = @AccessFailedCount, Modified = @Modified WHERE Id = @Id";

    private const string InsertCompany =
        "INSERT INTO Company (Id, Name, Slug, Created, Modified, Deleted) " +
        "VALUES (@Id, @Name, @Slug, @Now, @Now, 0)";

    private const string DeleteCompany = "DELETE FROM Company WHERE Id = @Id";

    private const string InsertLogin =
        "INSERT INTO ExternalLogin (Provider, ProviderKey, UserId, Created) " +
        "VALUES (@Provider, @ProviderKey, @UserId, @Now)";

    private const string DeleteLogin =
        "DELETE FROM ExternalLogin WHERE Provider = @Provider AND ProviderKey = @ProviderKey AND UserId = @UserId";

    private const string SelectLogins =
        "SELECT Provider AS LoginProvider, ProviderKey, Provider AS ProviderDisplayName " +
        "FROM ExternalLogin WHERE UserId = @UserId";

    public virtual async Task<AppUser?> FindByIdAsync(Guid id) =>
        (await db.GetManyAsync<AppUser>(SelectById, new() { ["Id"] = id })).FirstOrDefault();

    public virtual async Task<AppUser?> FindByNormalizedEmailAsync(string normalizedEmail) =>
        (await db.GetManyAsync<AppUser>(SelectByEmail, new() { ["NormalizedEmail"] = normalizedEmail })).FirstOrDefault();

    public virtual async Task<AppUser?> FindByLoginAsync(string provider, string providerKey) =>
        (await db.GetManyAsync<AppUser>(SelectByLogin,
            new() { ["Provider"] = provider, ["ProviderKey"] = providerKey })).FirstOrDefault();

    // Returns false when the email (any capitalisation) is already taken: the unique index decides, not a prior read.
    public virtual async Task<bool> TryInsertUserAsync(AppUser user)
    {
        try
        {
            await db.ExecuteAsync(InsertUser, new
            {
                user.Id, user.CompanyId, user.Email, user.NormalizedEmail, user.Name, user.PasswordHash, user.Role,
                user.IsActive, user.MustChangePassword, user.SecurityStamp, user.LockoutEnd, user.AccessFailedCount,
                user.Created, user.Modified,
            });
            return true;
        }
        catch (MySqlException ex) when (ex.Number == DuplicateKey)
        {
            return false;
        }
    }

    public virtual Task UpdateCredentialsAsync(AppUser user) =>
        db.ExecuteAsync(UpdateUserCredentials, new
        {
            user.Id, user.Email, user.NormalizedEmail, user.Name, user.PasswordHash, user.MustChangePassword,
            user.SecurityStamp, user.LockoutEnd, user.AccessFailedCount, Modified = DateTime.UtcNow,
        });

    // Creates a company and its first Admin together. When the email is already taken the company
    // is removed again, so a refused registration never leaves a company without a person.
    // Callers run this inside a transaction so any other failure rolls back both rows.
    public virtual async Task<bool> TryCreateCompanyWithUserAsync(string companyName, AppUser user)
    {
        var companyId = Guid.NewGuid();
        user.CompanyId = companyId;
        await db.ExecuteAsync(InsertCompany, new
        {
            Id = companyId,
            Name = companyName,
            Slug = BuildSlug(companyName, companyId),
            Now = DateTime.UtcNow,
        });

        if (await TryInsertUserAsync(user)) return true;

        await db.ExecuteAsync(DeleteCompany, new { Id = companyId });
        return false;
    }

    public virtual Task AddLoginAsync(Guid userId, string provider, string providerKey) =>
        db.ExecuteAsync(InsertLogin, new { Provider = provider, ProviderKey = providerKey, UserId = userId, Now = DateTime.UtcNow });

    public virtual Task RemoveLoginAsync(Guid userId, string provider, string providerKey) =>
        db.ExecuteAsync(DeleteLogin, new { Provider = provider, ProviderKey = providerKey, UserId = userId });

    public virtual async Task<IReadOnlyList<LoginRow>> GetLoginsAsync(Guid userId) =>
        await db.GetManyAsync<LoginRow>(SelectLogins, new() { ["UserId"] = userId });

    internal static string BuildSlug(string name, Guid companyId)
    {
        var chars = name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var slug = string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        if (slug.Length > 200) slug = slug[..200];
        return $"{slug}-{companyId:N}"[..Math.Min(255, slug.Length + 33)];
    }

    public sealed class LoginRow
    {
        public string LoginProvider { get; set; } = "";
        public string ProviderKey { get; set; } = "";
        public string ProviderDisplayName { get; set; } = "";
    }
}
