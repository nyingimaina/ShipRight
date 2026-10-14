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
        group.MapPost("/{id:guid}/deactivate", (Guid id, ICompanyContext company, TeamRepository team) => SetActiveAsync(id, false, company, team));
        group.MapPost("/{id:guid}/activate", (Guid id, ICompanyContext company, TeamRepository team) => SetActiveAsync(id, true, company, team));
    }

    private static async Task<IResult> ListAsync(ICompanyContext company, TeamRepository team, int page = 1, int pageSize = TeamRepository.DefaultPageSize)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, TeamRepository.MaxPageSize);
        var (items, total) = await team.ListAsync(company.RequireCompanyId(), page, pageSize);
        return Results.Ok(new TeamPageDto(items, total, page, pageSize));
    }

    private static async Task<IResult> CreateAsync(CreateTeammateRequest request, ICompanyContext company, RegistrationService registration, TeamRepository team)
    {
        var errors = IdentityValidation.Teammate(request);
        if (errors.Count > 0) return Results.ValidationProblem(errors);

        var created = await registration.CreateTeammateAsync(
            company.RequireCompanyId(), request.Email!, request.Name!, request.Role!, request.TemporaryPassword!);
        if (created is null)
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["email"] = ["That email is already in use."],
            });

        var dto = await team.FindAsync(created.CompanyId, created.Id);
        return Results.Created($"/api/team/{created.Id}", dto);
    }

    private static async Task<IResult> ChangeRoleAsync(Guid id, ChangeRoleRequest request, ICompanyContext company, TeamRepository team)
    {
        var errors = IdentityValidation.Role(request);
        if (errors.Count > 0) return Results.ValidationProblem(errors);

        var companyId = company.RequireCompanyId();
        return await team.ChangeRoleAsync(companyId, id, request.Role!, AppRoles.IsValid(request.ExpectedRole) ? request.ExpectedRole : null)
            switch
            {
                TeamChange.NotFound => Results.NotFound(),
                TeamChange.LastAdmin => Results.Json(new { message = LastAdminMessage, isError = true }, statusCode: 409),
                TeamChange.Conflict => Results.Json(new { message = ConflictMessage, isError = true }, statusCode: 409),
                _ => Results.Ok(await team.FindAsync(companyId, id)),
            };
    }

    private static async Task<IResult> SetActiveAsync(Guid id, bool active, ICompanyContext company, TeamRepository team)
    {
        var companyId = company.RequireCompanyId();
        return await team.SetActiveAsync(companyId, id, active) switch
        {
            TeamChange.NotFound => Results.NotFound(),
            TeamChange.LastAdmin => Results.Json(new { message = LastAdminMessage, isError = true }, statusCode: 409),
            _ => Results.Ok(await team.FindAsync(companyId, id)),
        };
    }
}
