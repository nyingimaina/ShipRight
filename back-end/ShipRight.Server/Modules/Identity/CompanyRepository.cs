using Rocket.Libraries.DatabaseIntegrator;
using ShipRight.Database;

namespace ShipRight.Modules.Identity;

public sealed class CompanyInfo
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public DateTime Created { get; set; }
}

/// <summary>
/// Companies themselves: create, rename and read. Membership is what grants access; this type never decides who may
/// see a company, so callers check the membership role first.
/// </summary>
public class CompanyRepository(IDatabaseHelper<Guid> db, ICompanyMembershipStore memberships)
{
    private const string Insert =
        "INSERT INTO Company (Id, Name, Slug, Created, Modified, Deleted) VALUES (@Id, @Name, @Slug, @Now, @Now, 0)";

    private const string Rename =
        "UPDATE Company SET Name = @Name, Modified = UTC_TIMESTAMP() WHERE Id = @Id AND Deleted = 0";

    private const string One =
        "SELECT Id, Name, Created FROM Company WHERE Id = @Id AND Deleted = 0 LIMIT 1";

    /// <summary>Creates a company and makes the caller its first Admin in one transaction.</summary>
    public virtual async Task<CompanyInfo> CreateAsync(string name, Guid ownerUserId)
    {
        var clean = name.Trim();
        var id = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await db.ExecuteAsync(Insert, new
        {
            Id = id,
            Name = clean,
            Slug = IdentityRepository.BuildSlug(clean, id),
            Now = now,
        });
        await memberships.EnsureAsync(ownerUserId, id, AppRoles.Admin);
        return new CompanyInfo { Id = id, Name = clean, Created = now };
    }

    public virtual async Task<bool> RenameAsync(Guid id, string name) =>
        await db.ExecuteAsync(Rename, new { Id = id, Name = name.Trim() }) > 0;

    public virtual async Task<CompanyInfo?> FindAsync(Guid id) =>
        (await db.GetManyAsync<CompanyInfo>(One, new() { ["Id"] = id })).FirstOrDefault();
}
