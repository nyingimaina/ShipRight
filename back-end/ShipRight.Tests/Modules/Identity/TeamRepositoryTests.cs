using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Identity;
using ShipRight.Tests.Modules.Tenancy;

namespace ShipRight.Tests.Modules.Identity;

[TestClass]
public class TeamRepositoryTests
{
    private static readonly Guid Company = Guid.NewGuid();
    private readonly RecordingDatabaseHelper _db = new();

    private TeamRepository Repository() => new(_db);

    [TestMethod]
    public async Task List_IsPagedInSql_OrderedByName_AndFilteredByCompany()
    {
        // R4, R9: paging and company filter live in the query, not after loading.
        await Repository().ListAsync(Company, page: 3, pageSize: 20);

        var page = _db.Statements[0];
        StringAssert.Contains(page.Sql, "CompanyId = @CompanyId");
        StringAssert.Contains(page.Sql, "ORDER BY Name");
        StringAssert.Contains(page.Sql, "LIMIT @Take OFFSET @Skip");
        var parameters = RecordingDatabaseHelper.ParametersOf(page.Parameters);
        Assert.AreEqual(Company, parameters["CompanyId"]);
        Assert.AreEqual(20, parameters["Take"]);
        Assert.AreEqual(40, parameters["Skip"]);
        StringAssert.Contains(_db.Statements[1].Sql, "COUNT(*)");
        StringAssert.Contains(_db.Statements[1].Sql, "CompanyId = @CompanyId");
    }

    [TestMethod]
    public async Task List_NeverSelectsPasswordHashes()
    {
        // API-004, R1
        await Repository().ListAsync(Company, 1, 20);

        Assert.IsFalse(_db.Statements[0].Sql.Contains("PasswordHash"));
        Assert.IsFalse(_db.Statements[0].Sql.Contains("SecurityStamp"));
    }

    [TestMethod]
    public void PageSizes_HaveAServerSideMaximum()
    {
        // R9, API-005
        Assert.AreEqual(20, TeamRepository.DefaultPageSize);
        Assert.AreEqual(50, TeamRepository.MaxPageSize);
    }

    [TestMethod]
    public async Task ChangeRole_KeepsALastAdminGuard_AndCompanyFilter_InTheSameUpdate()
    {
        // R4, R7: the check that another active Admin exists is inside the UPDATE, so two Admins cannot race it.
        await Repository().ChangeRoleAsync(Company, Guid.NewGuid(), AppRoles.Viewer, null);

        var sql = _db.Statements[0].Sql;
        StringAssert.StartsWith(sql, "UPDATE `User`");
        StringAssert.Contains(sql, "CompanyId = @CompanyId");
        StringAssert.Contains(sql, "EXISTS");
        StringAssert.Contains(sql, "o.Role = 'Admin' AND o.IsActive = 1");
        StringAssert.Contains(sql, "@ExpectedRole IS NULL OR Role = @ExpectedRole");
    }

    [TestMethod]
    public async Task ChangeRole_Applied_WhenTheGuardedUpdateChangedARow()
    {
        // R7
        Assert.AreEqual(TeamChange.Applied, await Repository().ChangeRoleAsync(Company, Guid.NewGuid(), AppRoles.Viewer, null));
    }

    [TestMethod]
    public async Task ChangeRole_OfSomeoneInAnotherCompany_IsNotFound()
    {
        // R4, R7: nothing matched and nobody by that id exists in this company.
        _db.ExecuteResult = 0;

        Assert.AreEqual(TeamChange.NotFound, await Repository().ChangeRoleAsync(Company, Guid.NewGuid(), AppRoles.Viewer, null));
    }

    [TestMethod]
    public async Task ChangeRole_RefusedForTheLastAdmin_IsReportedAsLastAdmin()
    {
        // R7
        _db.ExecuteResult = 0;
        _db.ReturnOneBlankRow = true;
        _db.CustomizeBlankRow = row => Set(row, "Role", AppRoles.Admin, "IsActive", true);

        Assert.AreEqual(TeamChange.LastAdmin, await Repository().ChangeRoleAsync(Company, Guid.NewGuid(), AppRoles.Viewer, null));
    }

    [TestMethod]
    public async Task ChangeRole_ToTheRoleTheyAlreadyHave_DoesNothing()
    {
        // R7, R8: repeating a change is not an error.
        _db.ExecuteResult = 0;
        _db.ReturnOneBlankRow = true;
        _db.CustomizeBlankRow = row => Set(row, "Role", AppRoles.Viewer, "IsActive", true);

        Assert.AreEqual(TeamChange.NoChange, await Repository().ChangeRoleAsync(Company, Guid.NewGuid(), AppRoles.Viewer, null));
    }

    [TestMethod]
    public async Task ChangeRole_WhenSomeoneElseChangedItFirst_IsAConflictNotASilentOverwrite()
    {
        // R7: the later save is not silently lost.
        _db.ExecuteResult = 0;
        _db.ReturnOneBlankRow = true;
        _db.CustomizeBlankRow = row => Set(row, "Role", AppRoles.Operator, "IsActive", true);

        Assert.AreEqual(TeamChange.Conflict, await Repository().ChangeRoleAsync(Company, Guid.NewGuid(), AppRoles.Viewer, AppRoles.Viewer));
    }

    [TestMethod]
    public async Task SetActive_UpdateKeepsCompanyFilterAndLastAdminGuard_AndRotatesTheStamp()
    {
        // R4, R8: switching off ends a signed-in person's access within the minute.
        await Repository().SetActiveAsync(Company, Guid.NewGuid(), false);

        var sql = _db.Statements[0].Sql;
        StringAssert.Contains(sql, "CompanyId = @CompanyId");
        StringAssert.Contains(sql, "EXISTS");
        StringAssert.Contains(sql, "SecurityStamp = ");
        Assert.IsFalse(sql.Contains("DELETE"), "switching off must not delete anything");
    }

    [TestMethod]
    public async Task SetActive_OnSomeoneAlreadyOn_DoesNothingWithoutError()
    {
        // R8
        _db.ExecuteResult = 0;
        _db.ReturnOneBlankRow = true;
        _db.CustomizeBlankRow = row => Set(row, "Role", AppRoles.Operator, "IsActive", true);

        Assert.AreEqual(TeamChange.NoChange, await Repository().SetActiveAsync(Company, Guid.NewGuid(), true));
    }

    [TestMethod]
    public async Task SetActive_OffForTheLastActiveAdmin_IsRefused()
    {
        // R8
        _db.ExecuteResult = 0;
        _db.ReturnOneBlankRow = true;
        _db.CustomizeBlankRow = row => Set(row, "Role", AppRoles.Admin, "IsActive", true);

        Assert.AreEqual(TeamChange.LastAdmin, await Repository().SetActiveAsync(Company, Guid.NewGuid(), false));
    }

    [TestMethod]
    public async Task SetActive_OfSomeoneInAnotherCompany_IsNotFound()
    {
        // R8
        _db.ExecuteResult = 0;

        Assert.AreEqual(TeamChange.NotFound, await Repository().SetActiveAsync(Company, Guid.NewGuid(), false));
    }

    private static void Set(object row, string first, object firstValue, string second, object secondValue)
    {
        row.GetType().GetProperty(first)!.SetValue(row, firstValue);
        row.GetType().GetProperty(second)!.SetValue(row, secondValue);
    }
}
