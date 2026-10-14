using Rocket.Libraries.DatabaseIntegrator;

namespace ShipRight.Modules.Identity;

public enum TeamChange { Applied, NotFound, NoChange, LastAdmin, Conflict }

// Every query carries CompanyId, so a person from another company is simply not found (R4).
public class TeamRepository(IDatabaseHelper<Guid> db)
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 50;

    private const string SelectPage =
        "SELECT Id, Email, Name, Role, IsActive, Created FROM `User` " +
        "WHERE CompanyId = @CompanyId AND Deleted = 0 ORDER BY Name, Id LIMIT @Take OFFSET @Skip";

    private const string CountAll =
        "SELECT COUNT(*) FROM `User` WHERE CompanyId = @CompanyId AND Deleted = 0";

    private const string SelectOne =
        "SELECT Id, Email, Name, Role, IsActive, Created FROM `User` " +
        "WHERE Id = @Id AND CompanyId = @CompanyId AND Deleted = 0";

    // The 'another active Admin exists' test sits in the same UPDATE, so two Admins acting at once cannot
    // both pass a separate check and leave the company with no active Admin (R7, R8).
    private const string ChangeRoleSql =
        "UPDATE `User` SET Role = @Role, Modified = UTC_TIMESTAMP() " +
        "WHERE Id = @Id AND CompanyId = @CompanyId AND Deleted = 0 AND Role <> @Role " +
        "AND (@ExpectedRole IS NULL OR Role = @ExpectedRole) " +
        "AND (@Role = 'Admin' OR Role <> 'Admin' OR IsActive = 0 OR EXISTS (" +
        "SELECT 1 FROM `User` o WHERE o.CompanyId = @CompanyId AND o.Id <> @Id AND o.Role = 'Admin' AND o.IsActive = 1 AND o.Deleted = 0))";

    // Switching off also changes the security stamp so a person who is signed in loses access on the next check.
    private const string SetActiveSql =
        "UPDATE `User` SET IsActive = @Active, SecurityStamp = REPLACE(UUID(), '-', ''), Modified = UTC_TIMESTAMP() " +
        "WHERE Id = @Id AND CompanyId = @CompanyId AND Deleted = 0 AND IsActive <> @Active " +
        "AND (@Active = 1 OR Role <> 'Admin' OR EXISTS (" +
        "SELECT 1 FROM `User` o WHERE o.CompanyId = @CompanyId AND o.Id <> @Id AND o.Role = 'Admin' AND o.IsActive = 1 AND o.Deleted = 0))";

    public async Task<(IReadOnlyList<TeamMemberDto> Items, int Total)> ListAsync(Guid companyId, int page, int pageSize)
    {
        var rows = await db.GetManyAsync<TeamRow>(SelectPage, new()
        {
            ["CompanyId"] = companyId,
            ["Take"] = pageSize,
            ["Skip"] = (page - 1) * pageSize,
        });
        var total = await db.ExecuteScalarAsync<int>(CountAll, new { CompanyId = companyId });
        return (rows.Select(r => r.ToDto()).ToList(), total);
    }

    public async Task<TeamMemberDto?> FindAsync(Guid companyId, Guid id) =>
        (await db.GetManyAsync<TeamRow>(SelectOne, new() { ["Id"] = id, ["CompanyId"] = companyId })).FirstOrDefault()?.ToDto();

    public async Task<TeamChange> ChangeRoleAsync(Guid companyId, Guid id, string role, string? expectedRole)
    {
        var changed = await db.ExecuteAsync(ChangeRoleSql,
            new { Id = id, CompanyId = companyId, Role = role, ExpectedRole = expectedRole });
        if (changed > 0) return TeamChange.Applied;

        var current = await FindAsync(companyId, id);
        if (current is null) return TeamChange.NotFound;
        if (current.Role == role) return TeamChange.NoChange;
        if (expectedRole is not null && current.Role != expectedRole) return TeamChange.Conflict;
        return TeamChange.LastAdmin;
    }

    public async Task<TeamChange> SetActiveAsync(Guid companyId, Guid id, bool active)
    {
        var changed = await db.ExecuteAsync(SetActiveSql, new { Id = id, CompanyId = companyId, Active = active });
        if (changed > 0) return TeamChange.Applied;

        var current = await FindAsync(companyId, id);
        if (current is null) return TeamChange.NotFound;
        return current.IsActive == active ? TeamChange.NoChange : TeamChange.LastAdmin;
    }

    private sealed class TeamRow
    {
        public Guid Id { get; set; }
        public string Email { get; set; } = "";
        public string Name { get; set; } = "";
        public string Role { get; set; } = "";
        public bool IsActive { get; set; }
        public DateTime Created { get; set; }

        public TeamMemberDto ToDto() => new(Id, Email, Name, Role, IsActive, Created);
    }
}
