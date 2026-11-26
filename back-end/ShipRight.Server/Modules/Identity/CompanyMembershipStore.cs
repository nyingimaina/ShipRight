using Rocket.Libraries.DatabaseIntegrator;

namespace ShipRight.Modules.Identity;

/// <summary>
/// One person's place in one company. A person may have several, each with its own role; User.CompanyId/Role keeps
/// mirroring the primary one so an older ShipRight build can still read the same database.
/// </summary>
public sealed record CompanyMembership
{
    public Guid Id { get; init; }
    public Guid UserId { get; init; }
    public Guid CompanyId { get; init; }
    public string Role { get; init; } = AppRoles.Viewer;
    public bool IsActive { get; init; } = true;
    public DateTime Created { get; init; }
    public DateTime Modified { get; init; }

    // Filled by the joins used by the switcher and the Team screen.
    public string CompanyName { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public bool AccountIsActive { get; init; } = true;
}

public interface ICompanyMembershipStore
{
    /// <summary>Every company this person may enter, newest membership first. Inactive memberships are hidden by default.</summary>
    Task<IReadOnlyList<CompanyMembership>> ListForUserAsync(Guid userId, bool includeInactive = false);

    /// <summary>The one membership that decides the role for the active company, or null when there is none.</summary>
    Task<CompanyMembership?> FindAsync(Guid userId, Guid companyId);

    Task<bool> IsAdminAsync(Guid userId, Guid companyId);

    /// <summary>Creates the membership if it is missing and leaves an existing one alone.</summary>
    Task EnsureAsync(Guid userId, Guid companyId, string role, bool isActive = true);

    /// <summary>Every person in a company, for the Team screen.</summary>
    Task<IReadOnlyList<CompanyMembership>> ListCompanyAsync(Guid companyId);

    /// <summary>One page of a company's people, paged in SQL so a large team never loads whole.</summary>
    Task<(IReadOnlyList<CompanyMembership> Items, int Total)> ListCompanyPageAsync(
        Guid companyId, int page, int pageSize);

    Task<CompanyMembership?> FindMemberAsync(Guid companyId, Guid userId);

    /// <summary>Changes one membership's role, refusing to remove the last active Admin of that company.</summary>
    Task<TeamChange> ChangeRoleAsync(Guid companyId, Guid userId, string role, string? expectedRole);

    /// <summary>Switches one membership on or off, keeping the account flag in step when this is the person's only company.</summary>
    Task<TeamChange> SetActiveAsync(Guid companyId, Guid userId, bool active);
}

public class CompanyMembershipStore(IDatabaseHelper<Guid> db) : ICompanyMembershipStore
{
    private const string ForUser =
        "SELECT m.Id, m.UserId, m.CompanyId, m.Role, m.IsActive, m.Created, m.Modified, " +
        "c.Name AS CompanyName, u.Email, u.Name, u.IsActive AS AccountIsActive " +
        "FROM CompanyMembership m " +
        "INNER JOIN Company c ON c.Id = m.CompanyId AND c.Deleted = 0 " +
        "INNER JOIN `User` u ON u.Id = m.UserId AND u.Deleted = 0 " +
        "WHERE m.UserId = @UserId AND (@IncludeInactive = 1 OR m.IsActive = 1) " +
        "ORDER BY c.Name, m.CompanyId";

    private const string One =
        "SELECT m.Id, m.UserId, m.CompanyId, m.Role, m.IsActive, m.Created, m.Modified, " +
        "c.Name AS CompanyName, u.Email, u.Name, u.IsActive AS AccountIsActive " +
        "FROM CompanyMembership m " +
        "INNER JOIN Company c ON c.Id = m.CompanyId AND c.Deleted = 0 " +
        "INNER JOIN `User` u ON u.Id = m.UserId AND u.Deleted = 0 " +
        "WHERE m.UserId = @UserId AND m.CompanyId = @CompanyId LIMIT 1";

    private const string Insert =
        "INSERT INTO CompanyMembership (Id, UserId, CompanyId, Role, IsActive, Created, Modified) " +
        "VALUES (@Id, @UserId, @CompanyId, @Role, @IsActive, @Now, @Now) " +
        "ON DUPLICATE KEY UPDATE UserId = UserId";

    private const string ForCompany =
        "SELECT m.Id, m.UserId, m.CompanyId, m.Role, m.IsActive, m.Created, m.Modified, " +
        "c.Name AS CompanyName, u.Email, u.Name, u.IsActive AS AccountIsActive " +
        "FROM CompanyMembership m " +
        "INNER JOIN Company c ON c.Id = m.CompanyId AND c.Deleted = 0 " +
        "INNER JOIN `User` u ON u.Id = m.UserId AND u.Deleted = 0 " +
        "WHERE m.CompanyId = @CompanyId ORDER BY u.Name, u.Id";

    private const string ForCompanyPage =
        "SELECT m.Id, m.UserId, m.CompanyId, m.Role, m.IsActive, m.Created, m.Modified, " +
        "c.Name AS CompanyName, u.Email, u.Name, u.IsActive AS AccountIsActive " +
        "FROM CompanyMembership m " +
        "INNER JOIN Company c ON c.Id = m.CompanyId AND c.Deleted = 0 " +
        "INNER JOIN `User` u ON u.Id = m.UserId AND u.Deleted = 0 " +
        "WHERE m.CompanyId = @CompanyId ORDER BY u.Name, m.UserId LIMIT @Take OFFSET @Skip";

    private const string CountCompany =
        "SELECT COUNT(*) FROM CompanyMembership m INNER JOIN `User` u ON u.Id = m.UserId AND u.Deleted = 0 " +
        "WHERE m.CompanyId = @CompanyId";

    // The last-Admin test is part of the UPDATE, so two Admins changing each other at once cannot both pass.
    private const string ChangeRole =
        "UPDATE CompanyMembership SET Role = @Role, Modified = UTC_TIMESTAMP() " +
        "WHERE UserId = @UserId AND CompanyId = @CompanyId AND Role <> @Role " +
        "AND (@ExpectedRole IS NULL OR Role = @ExpectedRole) " +
        "AND (@Role = 'Admin' OR Role <> 'Admin' OR IsActive = 0 OR EXISTS (" +
        "SELECT 1 FROM CompanyMembership o WHERE o.CompanyId = @CompanyId AND o.UserId <> @UserId " +
        "AND o.Role = 'Admin' AND o.IsActive = 1))";

    // The primary User row is mirrored only for the person's primary company, so old builds keep their answer.
    private const string MirrorPrimaryRole =
        "UPDATE `User` SET Role = @Role, SecurityStamp = REPLACE(UUID(), '-', ''), Modified = UTC_TIMESTAMP() " +
        "WHERE Id = @UserId AND CompanyId = @CompanyId AND Deleted = 0";

    private const string SetMembershipActive =
        "UPDATE CompanyMembership SET IsActive = @Active, Modified = UTC_TIMESTAMP() " +
        "WHERE UserId = @UserId AND CompanyId = @CompanyId AND IsActive <> @Active " +
        "AND (@Active = 1 OR Role <> 'Admin' OR EXISTS (" +
        "SELECT 1 FROM CompanyMembership o WHERE o.CompanyId = @CompanyId AND o.UserId <> @UserId " +
        "AND o.Role = 'Admin' AND o.IsActive = 1))";

    // A person with no other active company also has their account switched off, matching the single-company behaviour
    // people already know. Someone who belongs to other companies stays signed in there.
    private const string AccountOffIfAlone =
        "UPDATE `User` u SET u.IsActive = 0, u.SecurityStamp = REPLACE(UUID(), '-', ''), u.Modified = UTC_TIMESTAMP() " +
        "WHERE u.Id = @UserId AND u.Deleted = 0 AND NOT EXISTS (" +
        "SELECT 1 FROM CompanyMembership m WHERE m.UserId = @UserId AND m.IsActive = 1)";

    private const string AccountOnIfAlone =
        "UPDATE `User` u SET u.IsActive = 1, u.SecurityStamp = REPLACE(UUID(), '-', ''), u.Modified = UTC_TIMESTAMP() " +
        "WHERE u.Id = @UserId AND u.Deleted = 0 AND EXISTS (" +
        "SELECT 1 FROM CompanyMembership m WHERE m.UserId = @UserId AND m.IsActive = 1)";

    public virtual async Task<IReadOnlyList<CompanyMembership>> ListForUserAsync(Guid userId, bool includeInactive = false)
    {
        var rows = await db.GetManyAsync<CompanyMembership>(ForUser, new()
        {
            ["UserId"] = userId,
            ["IncludeInactive"] = includeInactive,
        });
        return rows.Where(Usable).ToList();
    }

    public virtual async Task<CompanyMembership?> FindAsync(Guid userId, Guid companyId) =>
        (await db.GetManyAsync<CompanyMembership>(One, new() { ["UserId"] = userId, ["CompanyId"] = companyId }))
            .FirstOrDefault(Usable);

    public virtual async Task<bool> IsAdminAsync(Guid userId, Guid companyId) =>
        await FindAsync(userId, companyId) is { IsActive: true, Role: AppRoles.Admin };

    public virtual async Task EnsureAsync(Guid userId, Guid companyId, string role, bool isActive = true)
    {
        if (!AppRoles.IsValid(role)) role = AppRoles.Viewer;
        await db.ExecuteAsync(Insert, new
        {
            Id = Guid.NewGuid(), UserId = userId, CompanyId = companyId, Role = role, IsActive = isActive,
            Now = DateTime.UtcNow,
        });
    }

    public virtual async Task<IReadOnlyList<CompanyMembership>> ListCompanyAsync(Guid companyId)
    {
        var rows = await db.GetManyAsync<CompanyMembership>(ForCompany, new() { ["CompanyId"] = companyId });
        return rows.Where(Usable).ToList();
    }

    public virtual async Task<(IReadOnlyList<CompanyMembership> Items, int Total)> ListCompanyPageAsync(
        Guid companyId, int page, int pageSize)
    {
        var rows = await db.GetManyAsync<CompanyMembership>(ForCompanyPage, new()
        {
            ["CompanyId"] = companyId,
            ["Take"] = pageSize,
            ["Skip"] = (page - 1) * pageSize,
        });
        var total = await db.ExecuteScalarAsync<int>(CountCompany, new { CompanyId = companyId });
        return (rows.Where(Usable).ToList(), total);
    }

    public virtual async Task<CompanyMembership?> FindMemberAsync(Guid companyId, Guid userId) =>
        (await ListCompanyAsync(companyId)).FirstOrDefault(m => m.UserId == userId);

    // A membership row always names a real person and a real company; ignoring blank rows keeps a stand-in or a
    // partially written row from ever granting access to "no company".
    private static bool Usable(CompanyMembership m) => m.UserId != Guid.Empty && m.CompanyId != Guid.Empty;

    public virtual async Task<TeamChange> ChangeRoleAsync(Guid companyId, Guid userId, string role, string? expectedRole)
    {
        var changed = await db.ExecuteAsync(ChangeRole,
            new { UserId = userId, CompanyId = companyId, Role = role, ExpectedRole = expectedRole });
        if (changed > 0)
        {
            await db.ExecuteAsync(MirrorPrimaryRole, new { UserId = userId, CompanyId = companyId, Role = role });
            return TeamChange.Applied;
        }

        var current = await FindMemberAsync(companyId, userId);
        if (current is null) return TeamChange.NotFound;
        if (current.Role == role) return TeamChange.NoChange;
        if (expectedRole is not null && current.Role != expectedRole) return TeamChange.Conflict;
        return TeamChange.LastAdmin;
    }

    public virtual async Task<TeamChange> SetActiveAsync(Guid companyId, Guid userId, bool active)
    {
        var changed = await db.ExecuteAsync(SetMembershipActive,
            new { UserId = userId, CompanyId = companyId, Active = active });
        if (changed > 0)
        {
            await db.ExecuteAsync(active ? AccountOnIfAlone : AccountOffIfAlone, new { UserId = userId });
            return TeamChange.Applied;
        }

        var current = await FindMemberAsync(companyId, userId);
        if (current is null) return TeamChange.NotFound;
        if (current.IsActive == active) return TeamChange.NoChange;
        return TeamChange.LastAdmin;
    }
}
