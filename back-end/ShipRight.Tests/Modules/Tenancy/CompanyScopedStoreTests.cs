using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Builds;
using ShipRight.Modules.Projects;
using ShipRight.Modules.Resources.Models;
using ShipRight.Modules.Resources.Stores;
using ShipRight.Modules.Scheduler;
using ShipRight.Modules.Servers;
using ShipRight.Modules.WatchBranch;
using ShipRight.Shared.Tenancy;

namespace ShipRight.Tests.Modules.Tenancy;

// R4: the company check is part of the SQL each store sends, taken from the signed-in person's claim only.
[TestClass]
public class CompanyScopedStoreTests
{
    private static readonly Guid CompanyA = Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000a");
    private static readonly Guid CompanyB = Guid.Parse("bbbbbbbb-0000-0000-0000-00000000000b");

    private RecordingDatabaseHelper _db = null!;
    private CompanyContext _context = null!;
    private CompanyScope _scope = null!;

    [TestInitialize]
    public void Setup()
    {
        _db = new RecordingDatabaseHelper();
        _context = new CompanyContext();
        _scope = new CompanyScope(_context, _db);
        CompanyContext.Set(CompanyA);
    }

    [TestCleanup]
    public void Cleanup() => CompanyContext.Set(null);

    private bool AnyStatementFiltersBy(Guid companyId) =>
        _db.Statements.Any(s => s.Sql.Contains("CompanyId", StringComparison.Ordinal)
            && RecordingDatabaseHelper.ParametersOf(s.Parameters).Values.Contains(companyId));

    private void AssertEveryStatementFiltersBy(Guid companyId)
    {
        Assert.IsTrue(_db.Statements.Count > 0);
        foreach (var (sql, parameters) in _db.Statements)
        {
            StringAssert.Contains(sql, "CompanyId");
            Assert.IsTrue(RecordingDatabaseHelper.ParametersOf(parameters).Values.Contains(companyId), sql);
        }
    }

    [TestMethod]
    public async Task ProjectStore_List_IsFilteredByCompanyInSql()
    {
        // R4
        await new CompanyScopedProjectStore(_db, _scope).GetAllAsync();

        AssertEveryStatementFiltersBy(CompanyA);
    }

    [TestMethod]
    public async Task ProjectStore_GetById_FiltersByIdAndCompanyTogether()
    {
        // R4: a record of another company is simply not returned by the query.
        var id = Guid.NewGuid();
        await new CompanyScopedProjectStore(_db, _scope).GetByIdAsync(id.ToString());

        AssertEveryStatementFiltersBy(CompanyA);
        Assert.IsTrue(RecordingDatabaseHelper.ParametersOf(_db.Statements[0].Parameters).Values.Contains(id));
    }

    [TestMethod]
    public async Task ProjectStore_Delete_CannotReachAnotherCompany()
    {
        // R4
        await new CompanyScopedProjectStore(_db, _scope).DeleteAsync(Guid.NewGuid().ToString());

        AssertEveryStatementFiltersBy(CompanyA);
    }

    [TestMethod]
    public async Task ProjectStore_NewProject_IsStampedWithTheCallersCompany()
    {
        // R4: never the empty company the old store wrote.
        var project = new ProjectConfig { Id = Guid.NewGuid().ToString(), Name = "demo" };
        await new CompanyScopedProjectStore(_db, _scope).SaveAsync(project);

        var insert = _db.Statements.Last();
        StringAssert.Contains(insert.Sql.ToUpperInvariant(), "INSERT");
        var values = RecordingDatabaseHelper.ParametersOf(insert.Parameters).Values;
        Assert.IsTrue(values.Contains(CompanyA));
        Assert.IsFalse(values.Contains(Guid.Empty));
    }

    [TestMethod]
    public async Task ProjectStore_ExistingProject_UpdateIsFilteredByCompany()
    {
        // R4
        var project = new ProjectConfig { Id = Guid.NewGuid().ToString(), Name = "demo" };
        _db.ReturnOneBlankRow = true;
        await new CompanyScopedProjectStore(_db, _scope).SaveAsync(project);

        StringAssert.Contains(_db.LastSql.ToUpperInvariant(), "UPDATE");

        Assert.IsTrue(_db.Statements.All(s => s.Sql.Contains("CompanyId")));
    }

    [TestMethod]
    public async Task Stores_WithNoCompany_FailClosed()
    {
        // R4: not signed in, not system work: nothing is read or written.
        CompanyContext.Set(null);

        await Assert.ThrowsExceptionAsync<CompanyScopeException>(() => new CompanyScopedProjectStore(_db, _scope).GetAllAsync());
        await Assert.ThrowsExceptionAsync<CompanyScopeException>(() => new CompanyScopedServerStore(_db, _scope).GetAllAsync());
        await Assert.ThrowsExceptionAsync<CompanyScopeException>(() => new CompanyScopedBuildStore(_db, _scope).QueryAsync(null, null, null, null, null, 1, 10));
        Assert.AreEqual(0, _db.Statements.Count);
    }

    [TestMethod]
    public async Task Stores_InSystemScope_ReadAcrossCompanies_ButOnlyThere()
    {
        // R4, R11: start-up scheduling lists every company's projects; a request never can.
        CompanyContext.Set(null);
        using (_context.BeginSystemScope())
            await new CompanyScopedProjectStore(_db, _scope).GetAllAsync();

        Assert.IsFalse(_db.Statements[0].Sql.Contains("CompanyId"));
        await Assert.ThrowsExceptionAsync<CompanyScopeException>(() => new CompanyScopedProjectStore(_db, _scope).GetAllAsync());
    }

    [TestMethod]
    public void SystemScopes_Nest_AndLeavingTheInnerOneKeepsTheOuter()
    {
        // R4, R11: building a scheduler inside a scheduler must not drop the system scope half-way.
        CompanyContext.Set(null);
        using (_context.BeginSystemScope())
        {
            using (_context.BeginSystemScope()) Assert.IsTrue(_context.IsSystem);
            Assert.IsTrue(_context.IsSystem);
        }
        Assert.IsFalse(_context.IsSystem);
    }

    [TestMethod]
    public void Counts_ForACallWithNoCompany_AreZero_WithoutTouchingTheDatabase()
    {
        // R4, BOOT-001: the public health check reports no company's numbers and does not fail.
        CompanyContext.Set(null);

        Assert.AreEqual(0, new CompanyScopedProjectStore(_db, _scope).Count);
        Assert.AreEqual(0, new CompanyScopedBuildStore(_db, _scope).Count);
        Assert.AreEqual(0, new CompanyScopedScriptResourceStore(_db, _scope).Count);
        Assert.AreEqual(0, _db.Statements.Count);
    }

    [TestMethod]
    public void Counts_ForASignedInPerson_AreCountedInSqlForTheirCompany()
    {
        // R4, PERF-001
        _ = new CompanyScopedProjectStore(_db, _scope).Count;

        AssertEveryStatementFiltersBy(CompanyA);
        StringAssert.Contains(_db.LastSql.ToUpperInvariant(), "COUNT(");
    }

    [TestMethod]
    public async Task ServerStore_IsFilteredByCompany()
    {
        // R4
        var store = new CompanyScopedServerStore(_db, _scope);
        await store.GetAllAsync();
        await store.GetByIdAsync(Guid.NewGuid().ToString());
        await store.DeleteAsync(Guid.NewGuid().ToString());

        AssertEveryStatementFiltersBy(CompanyA);
    }

    [TestMethod]
    public async Task BuildStore_ListCountAndGet_AreFilteredByCompany()
    {
        // R4
        var store = new CompanyScopedBuildStore(_db, _scope);
        await store.QueryAsync(Guid.NewGuid().ToString(), "Running,Failed", null, null, null, 1, 20);
        await store.CountQueryAsync(null, null, null, null, null);
        await store.GetByIdAsync(Guid.NewGuid().ToString());
        _ = store.Count;

        AssertEveryStatementFiltersBy(CompanyA);
    }

    [TestMethod]
    public async Task BuildStore_CountIsDoneInSql_NotByLoadingEveryBuild()
    {
        // PERF-001
        await new CompanyScopedBuildStore(_db, _scope).CountQueryAsync(null, null, null, null, null);

        StringAssert.Contains(_db.LastSql.ToUpperInvariant(), "COUNT(");
    }

    [TestMethod]
    public async Task BuildStore_StartedByBackgroundWork_TakesTheCompanyOfItsProject()
    {
        // R4: watch-branch builds have no signed-in person; the project's company is used, never the empty one.
        var projectId = Guid.NewGuid();
        _db.CompanyOfProject = CompanyB;
        CompanyContext.Set(null);
        using (_context.BeginSystemScope())
            await new CompanyScopedBuildStore(_db, _scope).SaveAsync(new BuildRecord { ProjectId = projectId.ToString() });

        var insert = _db.Statements.Last();
        Assert.IsTrue(RecordingDatabaseHelper.ParametersOf(insert.Parameters).Values.Contains(CompanyB));
    }

    [TestMethod]
    public async Task ResourceStores_AreFilteredByCompany()
    {
        // R4
        await new CompanyScopedDockerRegistryResourceStore(_db, _scope).GetAllAsync();
        await new CompanyScopedScriptResourceStore(_db, _scope).GetGlobalAsync();
        await new CompanyScopedPipelineResourceStore(_db, _scope).GetByProjectAsync(Guid.NewGuid().ToString());
        await new CompanyScopedCredentialResourceStore(_db, _scope).GetByIdAsync(Guid.NewGuid());
        await new CompanyScopedAwsProfileResourceStore(_db, _scope).GetByNameAsync("profile");
        await new CompanyScopedScriptResourceStore(_db, _scope).DeleteAsync(Guid.NewGuid());

        AssertEveryStatementFiltersBy(CompanyA);
    }

    [TestMethod]
    public async Task ResourceStore_NewResource_IsStampedWithTheCallersCompany()
    {
        // R4
        await new CompanyScopedCredentialResourceStore(_db, _scope)
            .SaveAsync(new CredentialResource { Id = Guid.NewGuid(), Name = "token" });

        var values = RecordingDatabaseHelper.ParametersOf(_db.Statements.Last().Parameters).Values;
        Assert.IsTrue(values.Contains(CompanyA));
        Assert.IsFalse(values.Contains(Guid.Empty));
    }

    [TestMethod]
    public async Task HistoryStores_AreFilteredByCompany()
    {
        // R4
        await new MariaDbBackupHistoryStore(_db, _scope).Query();
        await new MariaDbBackupHistoryStore(_db, _scope).GetSummary();
        await new MariaDbWatchBranchHistoryStore(_db, _scope).Query();

        AssertEveryStatementFiltersBy(CompanyA);
    }

    [TestMethod]
    public async Task HistoryStore_WrittenByScheduler_TakesTheCompanyOfItsProject()
    {
        // R4
        _db.CompanyOfProject = CompanyB;
        CompanyContext.Set(null);
        using (_context.BeginSystemScope())
            await new MariaDbWatchBranchHistoryStore(_db, _scope).Append(new WatchBranchHistoryRecord
            {
                ProjectId = Guid.NewGuid().ToString(),
                ScheduleId = Guid.NewGuid(),
                CorrelationId = Guid.NewGuid(),
            });

        Assert.IsTrue(RecordingDatabaseHelper.ParametersOf(_db.Statements.Last().Parameters).Values.Contains(CompanyB));
    }

    [TestMethod]
    public async Task Middleware_TakesCompanyFromTheClaim_NotFromHeadersOrQuery()
    {
        // R4: sending another company id in a header or query string changes nothing.
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(AppClaims.CompanyId, CompanyA.ToString())], "test")),
        };
        http.Request.Headers["X-Company-Id"] = CompanyB.ToString();
        http.Request.QueryString = new QueryString($"?companyId={CompanyB}");

        CompanyContext.Set(null);
        Guid? seen = null;
        await new CompanyContextMiddleware(_ => { seen = _context.CompanyId; return Task.CompletedTask; }).InvokeAsync(http);

        Assert.AreEqual(CompanyA, seen);
    }

    [TestMethod]
    public async Task Middleware_WithNoClaim_LeavesNoCompany()
    {
        // R4, R5
        CompanyContext.Set(CompanyB);
        Guid? seen = Guid.Empty;
        await new CompanyContextMiddleware(_ => { seen = _context.CompanyId; return Task.CompletedTask; }).InvokeAsync(new DefaultHttpContext());

        Assert.IsNull(seen);
    }
}
