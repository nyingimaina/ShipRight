using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using ShipRight.Shared.Tenancy;

namespace ShipRight.Modules.Identity;

/// <summary>
/// Companies a person belongs to, creating a new workspace, renaming one, and moving projects or servers between
/// companies. Every mutation checks the membership role in the exact company involved; nothing is trusted from the URL.
/// </summary>
public static class CompanyRouter
{
    public static void MapCompanyRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/companies");
        group.MapGet("", ListAsync);
        group.MapPost("", CreateAsync);
        group.MapPut("/{id:guid}", RenameAsync);

        app.MapPost("/api/projects/{projectId}/move", MoveProjectAsync);
        app.MapPost("/api/servers/{serverId}/move", MoveServerAsync);
    }

    private static async Task<IResult> ListAsync(
        ClaimsPrincipal principal, UserManager<AppUser> users, ICompanyMembershipStore memberships,
        ICompanyContext company, CompanyRepository companies)
    {
        var user = await users.GetUserAsync(principal);
        if (user is not { IsActive: true }) return Unauthorized();

        var current = company.CompanyId ?? user.CompanyId;
        var rows = await memberships.ListForUserAsync(user.Id);
        var items = rows.Count > 0
            ? rows.Select(m => new CompanyChoiceDto(m.CompanyId, m.CompanyName, m.Role, m.IsActive, m.CompanyId == current)).ToList()
            : await FallbackAsync(user, current, companies);

        return Results.Ok(new { companies = items, activeCompanyId = current });
    }

    private static async Task<IResult> CreateAsync(
        CreateCompanyRequest request, ClaimsPrincipal principal, UserManager<AppUser> users,
        IdentityRepository identity, ICompanyMembershipStore memberships, CompanyRepository companies, HttpContext http)
    {
        var user = await users.GetUserAsync(principal);
        if (user is not { IsActive: true }) return Unauthorized();

        var errors = ValidateName(request.Name);
        if (errors.Count > 0) return Results.ValidationProblem(errors);

        var created = await companies.CreateAsync(request.Name!, user.Id);
        // The new workspace is where the person wants to work next; land them there straight away.
        IdentitySession.Select(http, created.Id, AppRoles.Admin);
        return Results.Created($"/api/companies/{created.Id}",
            await IdentitySession.BuildAsync(user, identity, memberships, created.Id, AppRoles.Admin));
    }

    private static async Task<IResult> RenameAsync(
        Guid id, RenameCompanyRequest request, ClaimsPrincipal principal, UserManager<AppUser> users,
        ICompanyMembershipStore memberships, CompanyRepository companies)
    {
        var user = await users.GetUserAsync(principal);
        if (user is not { IsActive: true }) return Unauthorized();

        if (!await memberships.IsAdminAsync(user.Id, id))
            return Results.Json(new { message = "Only an Admin of that company can rename it.", isError = true },
                statusCode: 403);

        var errors = ValidateName(request.Name);
        if (errors.Count > 0) return Results.ValidationProblem(errors);

        if (!await companies.RenameAsync(id, request.Name!))
            return Results.NotFound(new { message = "That company no longer exists.", isError = true });

        return Results.Ok(new { id, name = request.Name!.Trim() });
    }

    private static async Task<IResult> MoveProjectAsync(
        string projectId, MoveResourceRequest request, ClaimsPrincipal principal, CompanyMoveService mover,
        CancellationToken ct)
    {
        if (!TryActor(principal, out var actor)) return Unauthorized();
        return ToResult(await mover.MoveProjectAsync(projectId, request.TargetCompanyId, actor, request.IncludeDependencies, ct));
    }

    private static async Task<IResult> MoveServerAsync(
        string serverId, MoveResourceRequest request, ClaimsPrincipal principal, CompanyMoveService mover,
        CancellationToken ct)
    {
        if (!TryActor(principal, out var actor)) return Unauthorized();
        return ToResult(await mover.MoveServerAsync(serverId, request.TargetCompanyId, actor, request.IncludeDependencies, ct));
    }

    private static IResult ToResult(MoveResult result) => result.Outcome switch
    {
        MoveOutcome.Moved => Results.Ok(new
        {
            moved = true,
            message = result.Message,
            projects = result.Projects,
            builds = result.Builds,
            backupHistory = result.BackupHistory,
            watchBranchHistory = result.WatchBranchHistory,
            schedules = result.Schedules,
            resources = result.Resources,
            tailnetCleared = result.TailnetCleared,
        }),
        MoveOutcome.Forbidden => Results.Json(new { message = result.Message, isError = true }, statusCode: 403),
        MoveOutcome.NotFound => Results.NotFound(new { message = "That resource no longer exists here.", isError = true }),
        MoveOutcome.SameCompany => Results.Json(new { message = "It is already in that company.", isError = true }, statusCode: 409),
        MoveOutcome.InvalidTarget => Results.Json(new { message = result.Message, isError = true }, statusCode: 400),
        MoveOutcome.NeedsServerMove or MoveOutcome.NeedsProjectMove or MoveOutcome.SharedResource =>
            Results.Json(new { message = result.Message, isError = true, code = result.Outcome.ToString(), serverName = result.ServerName },
                statusCode: 409),
        _ => Results.Json(new { message = "The move could not be completed.", isError = true }, statusCode: 409),
    };

    private static Dictionary<string, string[]> ValidateName(string? name)
    {
        var clean = IdentityValidation.Trimmed(name);
        return clean.Length switch
        {
            0 => new() { ["name"] = ["Enter a company name."] },
            > 200 => new() { ["name"] = ["Use at most 200 characters."] },
            _ => new(),
        };
    }

    private static bool TryActor(ClaimsPrincipal principal, out Guid actor) =>
        Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out actor);

    private static IResult Unauthorized() =>
        Results.Json(new { message = "Sign in required.", isError = true }, statusCode: 401);

    private static async Task<List<CompanyChoiceDto>> FallbackAsync(AppUser user, Guid current, CompanyRepository companies)
    {
        // A database from before memberships: one company, exactly as the old session reported it.
        var found = await companies.FindAsync(user.CompanyId);
        return [new CompanyChoiceDto(user.CompanyId, found?.Name ?? "", user.Role, true, user.CompanyId == current)];
    }
}
