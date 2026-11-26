using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Identity;
using ShipRight.Modules.Projects;
using ShipRight.Modules.Scheduler;
using ShipRight.Modules.Servers;
using ShipRight.Shared.Tenancy;
using ShipRight.Tests.Modules.Tenancy;

namespace ShipRight.Tests.Modules.Identity;

[TestClass]
public class CompanyMoveServiceTests
{
    private static readonly Guid Source = Guid.NewGuid();
    private static readonly Guid Target = Guid.NewGuid();
    private static readonly Guid Actor = Guid.NewGuid();
    private static readonly string ProjectId = Guid.NewGuid().ToString();
    private static readonly string ServerId = Guid.NewGuid().ToString();

    private sealed class Projects(params ProjectConfig[] projects) : IProjectStore
    {
        public int Count => projects.Length;
        public Task<List<ProjectConfig>> GetAllAsync() => Task.FromResult(projects.ToList());
        public Task<ProjectConfig?> GetByIdAsync(string id) =>
            Task.FromResult(projects.FirstOrDefault(p => p.Id == id));
        public Task<ProjectConfig?> GetByNameAsync(string name) =>
            Task.FromResult(projects.FirstOrDefault(p => p.Name == name));
        public Task SaveAsync(ProjectConfig project) => Task.CompletedTask;
        public Task DeleteAsync(string id) => Task.CompletedTask;
    }

    private sealed class Servers(params ServerConfig[] servers) : IServerStore
    {
        public Task<List<ServerConfig>> GetAllAsync() => Task.FromResult(servers.ToList());
        public Task<ServerConfig?> GetByIdAsync(string id) =>
            Task.FromResult(servers.FirstOrDefault(s => s.Id == id));
        public Task SaveAsync(ServerConfig server) => Task.CompletedTask;
        public Task DeleteAsync(string id) => Task.CompletedTask;
    }

    private sealed class Admins(params Guid[] companies) : ICompanyMembershipStore
    {
        public Task<IReadOnlyList<CompanyMembership>> ListForUserAsync(Guid userId, bool includeInactive = false) =>
            Task.FromResult<IReadOnlyList<CompanyMembership>>([]);
        public Task<CompanyMembership?> FindAsync(Guid userId, Guid companyId) => Task.FromResult<CompanyMembership?>(null);
        public Task<bool> IsAdminAsync(Guid userId, Guid companyId) => Task.FromResult(companies.Contains(companyId));
        public Task EnsureAsync(Guid userId, Guid companyId, string role, bool isActive = true) => Task.CompletedTask;
        public Task<IReadOnlyList<CompanyMembership>> ListCompanyAsync(Guid companyId) =>
            Task.FromResult<IReadOnlyList<CompanyMembership>>([]);
        public Task<(IReadOnlyList<CompanyMembership> Items, int Total)> ListCompanyPageAsync(
            Guid companyId, int page, int pageSize) =>
            Task.FromResult<(IReadOnlyList<CompanyMembership>, int)>(([], 0));
        public Task<CompanyMembership?> FindMemberAsync(Guid companyId, Guid userId) => Task.FromResult<CompanyMembership?>(null);
        public Task<TeamChange> ChangeRoleAsync(Guid companyId, Guid userId, string role, string? expectedRole) =>
            Task.FromResult(TeamChange.Applied);
        public Task<TeamChange> SetActiveAsync(Guid companyId, Guid userId, bool active) => Task.FromResult(TeamChange.Applied);
    }

    private sealed class Companies(RecordingDatabaseHelper db, ICompanyMembershipStore memberships, bool exists)
        : CompanyRepository(db, memberships)
    {
        public override Task<CompanyInfo?> FindAsync(Guid id) =>
            Task.FromResult(exists ? new CompanyInfo { Id = id, Name = "Destination" } : null);
    }

    private sealed class Schedules : IBackupScheduleStore
    {
        public int Moved { get; private set; }
        public Task<List<BackupScheduleDefinition>> GetAllAsync() => Task.FromResult(new List<BackupScheduleDefinition>());
        public Task<BackupScheduleDefinition?> GetByRuntimeIdAsync(Guid id) => Task.FromResult<BackupScheduleDefinition?>(null);
        public Task SaveAsync(BackupScheduleDefinition definition) => Task.CompletedTask;
        public Task DeleteByRuntimeIdAsync(Guid id) => Task.CompletedTask;
        public Task DeleteAsync(string id) => Task.CompletedTask;
        public Task<int> MoveProjectAsync(string projectId, Guid fromCompanyId, Guid toCompanyId)
        {
            Moved++;
            return Task.FromResult(1);
        }
    }

    private sealed class Context(Guid? companyId) : ICompanyContext
    {
        public Guid? CompanyId => companyId;
        public bool IsSystem => false;
        public Guid RequireCompanyId() => companyId ?? throw new CompanyScopeException("none");
        public IDisposable BeginSystemScope() => new Scope();
        private sealed class Scope : IDisposable { public void Dispose() { } }
    }

    private static (CompanyMoveService Mover, RecordingDatabaseHelper Db, Schedules Schedules) Build(
        IProjectStore projects, IServerStore servers, bool adminTarget = true)
    {
        var db = new RecordingDatabaseHelper();
        var memberships = new Admins(adminTarget ? [Source, Target] : [Source]);
        var schedules = new Schedules();
        var mover = new CompanyMoveService(
            db, projects, servers, new Context(Source), memberships,
            new Companies(db, memberships, exists: true), schedules);
        return (mover, db, schedules);
    }

    private static ProjectConfig Project(string id, string? serverId = null) => new()
    {
        Id = id, Name = "Shop", ServerId = serverId ?? ServerId,
        Database = new DatabaseConfig { DatabaseName = "shop" },
    };

    private static ServerConfig Server(string id, string? tailnet = null) =>
        new() { Id = id, Name = "web-1", Host = "10.0.0.1", TailnetId = tailnet ?? "" };

    [TestMethod]
    public async Task A_project_that_uses_a_server_cannot_move_without_deciding_about_it()
    {
        var (mover, db, _) = Build(new Projects(Project(ProjectId)), new Servers(Server(ServerId)));

        var result = await mover.MoveProjectAsync(ProjectId, Target, Actor, includeServer: false);

        Assert.AreEqual(MoveOutcome.NeedsServerMove, result.Outcome);
        Assert.AreEqual("web-1", result.ServerName);
        Assert.AreEqual(0, db.Statements.Count, "nothing is written until the person has decided");
    }

    [TestMethod]
    public async Task Moving_a_project_moves_its_builds_history_and_schedules()
    {
        var (mover, db, schedules) = Build(new Projects(Project(ProjectId)), new Servers(Server(ServerId)));

        var result = await mover.MoveProjectAsync(ProjectId, Target, Actor, includeServer: true);

        Assert.AreEqual(MoveOutcome.Moved, result.Outcome);
        Assert.AreEqual(1, result.Projects);
        Assert.AreEqual(1, schedules.Moved);
        Assert.IsTrue(db.Statements.Any(s => s.Sql.Contains("UPDATE Project SET CompanyId")));
        Assert.IsTrue(db.Statements.Any(s => s.Sql.Contains("UPDATE Build SET CompanyId")));
        Assert.IsTrue(db.Statements.Any(s => s.Sql.Contains("UPDATE BackupHistory SET CompanyId")));
        Assert.IsTrue(db.Statements.Any(s => s.Sql.Contains("UPDATE WatchBranchHistory SET CompanyId")));
        Assert.IsTrue(db.Statements.Any(s => s.Sql.Contains("UPDATE SshKey SET CompanyId")));
        Assert.IsTrue(db.Statements.Any(s => s.Sql.Contains("UPDATE Server SET CompanyId")));
    }

    [TestMethod]
    public async Task A_registry_used_only_by_this_project_travels_with_it()
    {
        var registry = Guid.NewGuid();
        var project = Project(ProjectId, "") with
        {
            Services = [new ServiceConfig { Name = "api", DockerRegistryResourceId = registry }],
        };
        var (mover, db, _) = Build(new Projects(project), new Servers());

        var result = await mover.MoveProjectAsync(ProjectId, Target, Actor, includeServer: false);

        Assert.AreEqual(MoveOutcome.Moved, result.Outcome);
        Assert.AreEqual(1, result.Resources);
        Assert.IsTrue(db.Statements.Any(s =>
            s.Sql.StartsWith("UPDATE DockerRegistryResource SET CompanyId") &&
            RecordingDatabaseHelper.ParametersOf(s.Parameters)["Id"]!.Equals(registry)));
    }

    [TestMethod]
    public async Task A_registry_still_used_by_a_staying_project_blocks_the_move()
    {
        var registry = Guid.NewGuid();
        var project = Project(ProjectId, "") with
        {
            Services = [new ServiceConfig { Name = "api", DockerRegistryResourceId = registry }],
        };
        var other = Project(Guid.NewGuid().ToString(), "") with
        {
            Services = [new ServiceConfig { Name = "web", DockerRegistryResourceId = registry }],
        };
        var (mover, db, _) = Build(new Projects(project, other), new Servers());

        var result = await mover.MoveProjectAsync(ProjectId, Target, Actor, includeServer: false);

        Assert.AreEqual(MoveOutcome.SharedResource, result.Outcome);
        StringAssert.Contains(result.Message!, "Docker registry");
        Assert.AreEqual(0, db.Statements.Count);
    }

    [TestMethod]
    public async Task Moving_the_server_also_brings_its_other_projects_so_nothing_points_at_another_company()
    {
        var second = Guid.NewGuid().ToString();
        var (mover, _, _) = Build(
            new Projects(Project(ProjectId), Project(second)),
            new Servers(Server(ServerId)));

        var result = await mover.MoveProjectAsync(ProjectId, Target, Actor, includeServer: true);

        Assert.AreEqual(MoveOutcome.Moved, result.Outcome);
        Assert.AreEqual(2, result.Projects);
    }

    [TestMethod]
    public async Task Moving_a_server_moves_its_projects_only_when_asked()
    {
        var (mover, db, _) = Build(new Projects(Project(ProjectId)), new Servers(Server(ServerId)));

        var blocked = await mover.MoveServerAsync(ServerId, Target, Actor, includeProjects: false);
        Assert.AreEqual(MoveOutcome.NeedsProjectMove, blocked.Outcome);
        Assert.AreEqual(0, db.Statements.Count);

        var moved = await mover.MoveServerAsync(ServerId, Target, Actor, includeProjects: true);
        Assert.AreEqual(MoveOutcome.Moved, moved.Outcome);
        Assert.AreEqual(1, moved.Projects);
        Assert.IsTrue(db.Statements.Any(s => s.Sql.Contains("UPDATE Project SET CompanyId")));
        Assert.IsTrue(db.Statements.Any(s => s.Sql.Contains("UPDATE Server SET CompanyId")));
    }

    [TestMethod]
    public async Task A_server_that_leaves_its_tailnet_loses_the_assignment_so_nothing_dangles()
    {
        var (mover, db, _) = Build(new Projects(), new Servers(Server(ServerId, "tailnet-1")));

        var result = await mover.MoveServerAsync(ServerId, Target, Actor, includeProjects: false);

        Assert.AreEqual(MoveOutcome.Moved, result.Outcome);
        Assert.IsTrue(result.TailnetCleared);
        var write = db.Statements.Single(s => s.Sql.Contains("UPDATE Server SET CompanyId"));
        var data = RecordingDatabaseHelper.ParametersOf(write.Parameters)["Data"]?.ToString() ?? "";
        Assert.IsFalse(data.Contains("tailnet-1"), "the new company cannot see the old tailnet");
    }

    [TestMethod]
    public async Task Only_an_admin_of_both_companies_can_move()
    {
        var (mover, db, _) = Build(new Projects(Project(ProjectId, "")), new Servers(), adminTarget: false);

        var result = await mover.MoveProjectAsync(ProjectId, Target, Actor, includeServer: false);

        Assert.AreEqual(MoveOutcome.Forbidden, result.Outcome);
        StringAssert.Contains(result.Message!, "Admin");
        Assert.AreEqual(0, db.Statements.Count);
    }

    [TestMethod]
    public async Task Moving_into_the_same_company_is_a_no_op()
    {
        var (mover, db, _) = Build(new Projects(Project(ProjectId, "")), new Servers());

        var result = await mover.MoveProjectAsync(ProjectId, Source, Actor, includeServer: false);

        Assert.AreEqual(MoveOutcome.SameCompany, result.Outcome);
        Assert.AreEqual(0, db.Statements.Count);
    }
}
