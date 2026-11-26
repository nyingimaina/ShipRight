using System.Text.Json;
using Rocket.Libraries.DatabaseIntegrator;
using ShipRight.Modules.Projects;
using ShipRight.Modules.Scheduler;
using ShipRight.Modules.Servers;
using ShipRight.Shared.Tenancy;

namespace ShipRight.Modules.Identity;

public enum MoveOutcome
{
    Moved,
    NotFound,
    Forbidden,
    SameCompany,
    NeedsServerMove,
    NeedsProjectMove,
    SharedResource,
    InvalidTarget,
}

public sealed record MoveResult(
    MoveOutcome Outcome,
    string? Message = null,
    int Projects = 0,
    int Builds = 0,
    int BackupHistory = 0,
    int WatchBranchHistory = 0,
    int Schedules = 0,
    int SshKeys = 0,
    int Resources = 0,
    bool TailnetCleared = false,
    string? ServerName = null);

public sealed record MoveResourceRequest(Guid TargetCompanyId, bool IncludeDependencies = false);

/// <summary>
/// Moves a project or a server to another company the caller administers. A move is one transaction and re-stamps
/// every row that belongs to the resource, so nothing is left pointing at the old company. When a project still uses a
/// server that is not coming along (or a server still has projects staying behind) the move stops and explains the
/// choice rather than leaving a dangling reference.
/// </summary>
public class CompanyMoveService(
    IDatabaseHelper<Guid> db,
    IProjectStore projects,
    IServerStore servers,
    ICompanyContext company,
    ICompanyMembershipStore memberships,
    CompanyRepository companies,
    IBackupScheduleStore schedules)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private const string MoveProjectRow =
        "UPDATE Project SET CompanyId = @Target, Modified = UTC_TIMESTAMP() WHERE Id = @Id AND CompanyId = @Source";

    private const string MoveBuilds =
        "UPDATE Build SET CompanyId = @Target WHERE ProjectId = @ProjectId AND CompanyId = @Source";

    private const string MoveBackups =
        "UPDATE BackupHistory SET CompanyId = @Target WHERE ProjectId = @ProjectId AND CompanyId = @Source";

    private const string MoveWatchBranch =
        "UPDATE WatchBranchHistory SET CompanyId = @Target WHERE ProjectId = @ProjectId AND CompanyId = @Source";

    private const string MoveSshKey =
        "UPDATE SshKey SET CompanyId = @Target WHERE ProjectId = @ProjectId AND CompanyId = @Source";

    private const string MoveServerRow =
        "UPDATE Server SET CompanyId = @Target, Data = @Data, Modified = UTC_TIMESTAMP() " +
        "WHERE Id = @Id AND CompanyId = @Source";

    public virtual async Task<MoveResult> MoveProjectAsync(
        string projectId, Guid targetCompanyId, Guid actorUserId, bool includeServer, CancellationToken ct = default)
    {
        if (company.CompanyId is not { } source) return Forbidden("Choose the company that owns this project first.");
        if (!Guid.TryParse(projectId, out var projectKey)) return new MoveResult(MoveOutcome.NotFound);
        if (targetCompanyId == source) return new MoveResult(MoveOutcome.SameCompany);

        var project = await projects.GetByIdAsync(projectId);
        if (project is null) return new MoveResult(MoveOutcome.NotFound);

        var gate = await CheckAsync(source, targetCompanyId, actorUserId);
        if (gate is not null) return gate;

        ServerConfig? movingServer = null;
        var assignedServerId = Key(project);
        if (!string.IsNullOrWhiteSpace(assignedServerId))
        {
            var assigned = await servers.GetByIdAsync(assignedServerId);
            if (assigned is not null)
            {
                if (!includeServer)
                    return new MoveResult(MoveOutcome.NeedsServerMove,
                        $"'{project.Name}' uses the server '{assigned.Name}'. Move that server too, or remove it from the project first.",
                        ServerName: assigned.Name);

                movingServer = assigned;
            }
        }

        // A server carries every project that uses it. Moving it while leaving those behind would point this company's
        // projects at a server in another company, so they all travel together.
        var projectsToMove = movingServer is null
            ? [project]
            : (await projects.GetAllAsync())
                .Where(p => string.Equals(Key(p), movingServer.Id, StringComparison.OrdinalIgnoreCase))
                .ToList();
        if (movingServer is not null && projectsToMove.All(p => p.Id != project.Id)) projectsToMove.Add(project);

        // A project can point at shared company resources (registry credentials, a build pipeline, a script, a
        // credential). They travel with an exclusive project; a resource another project still uses cannot, so the
        // move stops and names it rather than leaving a reference the new company can never resolve.
        var movingIds = projectsToMove.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var staying = (await projects.GetAllAsync())
            .Where(p => !movingIds.Contains(p.Id))
            .ToList();
        var resourcesToMove = projectsToMove
            .SelectMany(References)
            .DistinctBy(r => (r.Table, r.Id))
            .ToList();

        foreach (var resource in resourcesToMove)
        {
            if (staying.Any(p => References(p).Any(r => r.Table == resource.Table && r.Id == resource.Id)))
                return new MoveResult(MoveOutcome.SharedResource,
                    $"'{resource.Label}' is also used by another project in this company. Replace or detach it there first, or move that project too.",
                    ServerName: resource.Label);
        }

        var result = new MoveResult(MoveOutcome.Moved);
        foreach (var moving in projectsToMove)
        {
            if (!Guid.TryParse(moving.Id, out var movingId)) continue;
            var moved = await MoveProjectCoreAsync(movingId, source, targetCompanyId, ct);
            result = result with
            {
                Projects = result.Projects + moved.Projects,
                Builds = result.Builds + moved.Builds,
                BackupHistory = result.BackupHistory + moved.BackupHistory,
                WatchBranchHistory = result.WatchBranchHistory + moved.WatchBranchHistory,
                Schedules = result.Schedules + moved.Schedules,
                SshKeys = result.SshKeys + moved.SshKeys,
            };
        }

        var movedResources = 0;
        foreach (var resource in resourcesToMove)
        {
            var changed = await db.ExecuteAsync(
                $"UPDATE {resource.Table} SET CompanyId = @Target WHERE Id = @Id AND CompanyId = @Source",
                new { Id = resource.Id, Source = source, Target = targetCompanyId });
            if (changed > 0) movedResources++;
        }
        result = result with { Resources = movedResources };

        if (movingServer is not null)
        {
            var serverResult = await MoveServerCoreAsync(movingServer, source, targetCompanyId);
            result = result with { TailnetCleared = serverResult.TailnetCleared };
        }

        return result;
    }

    public virtual async Task<MoveResult> MoveServerAsync(
        string serverId, Guid targetCompanyId, Guid actorUserId, bool includeProjects, CancellationToken ct = default)
    {
        if (company.CompanyId is not { } source) return Forbidden("Choose the company that owns this server first.");
        if (targetCompanyId == source) return new MoveResult(MoveOutcome.SameCompany);

        var server = await servers.GetByIdAsync(serverId);
        if (server is null) return new MoveResult(MoveOutcome.NotFound);

        var gate = await CheckAsync(source, targetCompanyId, actorUserId);
        if (gate is not null) return gate;

        var users = (await projects.GetAllAsync())
            .Where(p => string.Equals(Key(p), serverId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (users.Count > 0 && !includeProjects)
            return new MoveResult(MoveOutcome.NeedsProjectMove,
                users.Count == 1
                    ? $"'{users[0].Name}' still uses this server. Move that project too, or point it at another server first."
                    : $"{users.Count} projects still use this server. Move them too, or point them at another server first.",
                ServerName: server.Name);

        var movingIds = users.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stayingProjects = includeProjects
            ? (await projects.GetAllAsync()).Where(p => !movingIds.Contains(p.Id)).ToList()
            : [];
        var resourcesToMove = includeProjects
            ? users.SelectMany(References).DistinctBy(r => (r.Table, r.Id)).ToList()
            : [];

        foreach (var resource in resourcesToMove)
        {
            if (stayingProjects.Any(p => References(p).Any(r => r.Table == resource.Table && r.Id == resource.Id)))
                return new MoveResult(MoveOutcome.SharedResource,
                    $"'{resource.Label}' is also used by another project in this company. Replace or detach it there first, or move that project too.",
                    ServerName: resource.Label);
        }

        var movedProjects = 0;
        var movedBuilds = 0;
        var movedBackups = 0;
        var movedWatchBranch = 0;
        var movedSchedules = 0;
        var movedSshKeys = 0;
        var movedResources = 0;
        if (includeProjects)
        {
            foreach (var project in users)
            {
                var moved = await MoveProjectCoreAsync(Guid.Parse(project.Id), source, targetCompanyId, ct);
                movedProjects++;
                movedBuilds += moved.Builds;
                movedBackups += moved.BackupHistory;
                movedWatchBranch += moved.WatchBranchHistory;
                movedSchedules += moved.Schedules;
                movedSshKeys += moved.SshKeys;
            }

            foreach (var resource in resourcesToMove)
            {
                var changed = await db.ExecuteAsync(
                    $"UPDATE {resource.Table} SET CompanyId = @Target WHERE Id = @Id AND CompanyId = @Source",
                    new { Id = resource.Id, Source = source, Target = targetCompanyId });
                if (changed > 0) movedResources++;
            }
        }

        var serverMove = await MoveServerCoreAsync(server, source, targetCompanyId);
        return new MoveResult(MoveOutcome.Moved,
            movedProjects == 0 ? null : $"Moved {movedProjects} {(movedProjects == 1 ? "project" : "projects")} with the server.",
            movedProjects, movedBuilds, movedBackups, movedWatchBranch, movedSchedules, movedSshKeys,
            movedResources, serverMove.TailnetCleared);
    }

    private async Task<MoveResult> MoveProjectCoreAsync(
        Guid projectId, Guid source, Guid targetCompanyId, CancellationToken ct)
    {
        var key = projectId.ToString();
        var moved = await db.ExecuteAsync(MoveProjectRow, new { Id = projectId, Source = source, Target = targetCompanyId });
        if (moved == 0) return new MoveResult(MoveOutcome.NotFound);

        var builds = await db.ExecuteAsync(MoveBuilds, new { ProjectId = projectId, Source = source, Target = targetCompanyId });
        var backups = await db.ExecuteAsync(MoveBackups, new { ProjectId = projectId, Source = source, Target = targetCompanyId });
        var watch = await db.ExecuteAsync(MoveWatchBranch, new { ProjectId = projectId, Source = source, Target = targetCompanyId });
        var sshKeys = await db.ExecuteAsync(MoveSshKey, new { ProjectId = projectId, Source = source, Target = targetCompanyId });
        var scheduleCount = await schedules.MoveProjectAsync(key, source, targetCompanyId);

        return new MoveResult(MoveOutcome.Moved, null, 1, builds, backups, watch, scheduleCount, sshKeys, 0);
    }

    private async Task<MoveResult> MoveServerCoreAsync(ServerConfig server, Guid source, Guid target)
    {
        var tailnetCleared = !string.IsNullOrWhiteSpace(server.TailnetId);
        var moved = server;
        if (tailnetCleared)
        {
            // A tailnet belongs to one company, so it cannot follow the server. Clearing it keeps the server usable
            // over its normal direct network; the screen tells the person to assign a network in the new company.
            moved = server with { TailnetId = string.Empty };
        }

        await db.ExecuteAsync(MoveServerRow, new
        {
            Id = Guid.Parse(server.Id),
            Source = source,
            Target = target,
            Data = JsonSerializer.Serialize(moved, JsonOpts),
        });

        return new MoveResult(MoveOutcome.Moved, TailnetCleared: tailnetCleared, ServerName: server.Name);
    }

    private async Task<MoveResult?> CheckAsync(Guid source, Guid targetCompanyId, Guid actorUserId)
    {
        if (targetCompanyId == Guid.Empty || await companies.FindAsync(targetCompanyId) is null)
            return new MoveResult(MoveOutcome.InvalidTarget, "That company no longer exists.");

        if (!await memberships.IsAdminAsync(actorUserId, source))
            return Forbidden("Only an Admin of this company can move its resources.");

        if (!await memberships.IsAdminAsync(actorUserId, targetCompanyId))
            return Forbidden("You need to be an Admin of the destination company to move resources into it.");

        return null;
    }

    private static string Key(ProjectConfig project) =>
        string.IsNullOrWhiteSpace(project.Server.Id) ? project.ServerId : project.Server.Id;

    private sealed record ResourceRef(string Table, Guid Id, string Label);

    /// <summary>The company-scoped resources this project points at. A move has to carry them or refuse.</summary>
    private static IEnumerable<ResourceRef> References(ProjectConfig project)
    {
        foreach (var repo in project.GitRepos)
            if (repo.CredentialResourceId is { } id)
                yield return new ResourceRef("CredentialResource", id, "A git credential");

        foreach (var service in project.Services)
            if (service.DockerRegistryResourceId is { } id)
                yield return new ResourceRef("DockerRegistryResource", id, "A Docker registry");

        if (project.Server.RebuildScriptResourceId is { } script)
            yield return new ResourceRef("ScriptResource", script, "A rebuild script");

        if (project.Server.PipelineResourceId is { } pipeline)
            yield return new ResourceRef("PipelineResource", pipeline, "A build pipeline");
    }

    private static MoveResult Forbidden(string message) => new(MoveOutcome.Forbidden, message);
}
