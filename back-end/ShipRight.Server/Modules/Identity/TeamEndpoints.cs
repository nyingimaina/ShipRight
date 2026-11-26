using ShipRight.Shared.Tenancy;

namespace ShipRight.Modules.Identity;

// Admin-only (enforced by RoleAccessRules for everything under /api/team). The company always comes from the
// signed-in Admin's own claim; nothing in the URL or body can name a different one.
public static class TeamEndpoints
{
    internal const string LastAdminMessage = "Every company needs at least one active Admin. Make someone else an Admin first.";
    internal const string ConflictMessage = "This person was changed by someone else. Refresh the list and try again.";

    public static void MapTeamRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/team");
        group.MapGet("", ListAsync);
        group.MapPost("", CreateAsync);
        group.MapPut("/{id:guid}/role", ChangeRoleAsync);
        group.MapPost("/{id:guid}/deactivate", (Guid id, ICompanyContext company, TeamRepository team, ICompanyMembershipStore m) =>
            SetActiveAsync(id, false, company, team, m));
        group.MapPost("/{id:guid}/activate", (Guid id, ICompanyContext company, TeamRepository team, ICompanyMembershipStore m) =>
            SetActiveAsync(id, true, company, team, m));
    }

    private static async Task<IResult> ListAsync(
        ICompanyContext company, TeamRepository team, ICompanyMembershipStore memberships,
        int page = 1, int pageSize = TeamRepository.DefaultPageSize)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, TeamRepository.MaxPageSize);
        var companyId = company.RequireCompanyId();

        var (rows, total) = await memberships.ListCompanyPageAsync(companyId, page, pageSize);
        if (total == 0)
        {
            // A database from before memberships: keep listing the people on the company exactly as before.
            var (legacy, legacyTotal) = await team.ListAsync(companyId, page, pageSize);
            return Results.Ok(new TeamPageDto(legacy, legacyTotal, page, pageSize));
        }

        return Results.Ok(new TeamPageDto(rows.Select(ToDto).ToList(), total, page, pageSize));
    }

    private static async Task<IResult> CreateAsync(
        CreateTeammateRequest request, ICompanyContext company, RegistrationService registration,
        TeamRepository team, ICompanyMembershipStore memberships)
    {
        var errors = IdentityValidation.Teammate(request);
        if (errors.Count > 0) return Results.ValidationProblem(errors);

        var created = await registration.CreateTeammateWithMembershipAsync(
            company.RequireCompanyId(), request.Email!, request.Name!, request.Role!, request.TemporaryPassword!);
        if (created is null)
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["email"] = ["That email is already in use."],
            });

        var dto = await FindAsync(company.RequireCompanyId(), created.Id, team, memberships);
        return Results.Created($"/api/team/{created.Id}", dto);
    }

    private static async Task<IResult> ChangeRoleAsync(
        Guid id, ChangeRoleRequest request, ICompanyContext company, TeamRepository team, ICompanyMembershipStore memberships)
    {
        var errors = IdentityValidation.Role(request);
        if (errors.Count > 0) return Results.ValidationProblem(errors);

        var companyId = company.RequireCompanyId();
        var expected = AppRoles.IsValid(request.ExpectedRole) ? request.ExpectedRole : null;
        var change = await memberships.ChangeRoleAsync(companyId, id, request.Role!, expected);
        if (change == TeamChange.NotFound && !await HasMembershipsAsync(companyId, memberships))
            change = await team.ChangeRoleAsync(companyId, id, request.Role!, expected);

        return change switch
        {
            TeamChange.NotFound => Results.NotFound(),
            TeamChange.LastAdmin => Results.Json(new { message = LastAdminMessage, isError = true }, statusCode: 409),
            TeamChange.Conflict => Results.Json(new { message = ConflictMessage, isError = true }, statusCode: 409),
            _ => Results.Ok(await FindAsync(companyId, id, team, memberships)),
        };
    }

    private static async Task<IResult> SetActiveAsync(
        Guid id, bool active, ICompanyContext company, TeamRepository team, ICompanyMembershipStore memberships)
    {
        var companyId = company.RequireCompanyId();
        var change = await memberships.SetActiveAsync(companyId, id, active);
        if (change == TeamChange.NotFound && !await HasMembershipsAsync(companyId, memberships))
            change = await team.SetActiveAsync(companyId, id, active);

        return change switch
        {
            TeamChange.NotFound => Results.NotFound(),
            TeamChange.LastAdmin => Results.Json(new { message = LastAdminMessage, isError = true }, statusCode: 409),
            _ => Results.Ok(await FindAsync(companyId, id, team, memberships)),
        };
    }

    private static async Task<TeamMemberDto?> FindAsync(
        Guid companyId, Guid userId, TeamRepository team, ICompanyMembershipStore memberships)
    {
        var member = await memberships.FindMemberAsync(companyId, userId);
        if (member is not null) return ToDto(member);
        return (await team.FindAsync(companyId, userId));
    }

    private static async Task<bool> HasMembershipsAsync(Guid companyId, ICompanyMembershipStore memberships) =>
        (await memberships.ListCompanyAsync(companyId)).Count > 0;

    private static TeamMemberDto ToDto(CompanyMembership m) =>
        new(m.UserId, m.Email, m.Name, m.Role, m.IsActive && m.AccountIsActive, m.Created);
}
