using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Identity;
using ShipRight.Tests.Modules.Tenancy;

namespace ShipRight.Tests.Modules.Identity;

[TestClass]
public class CompanyMembershipStoreTests
{
    private readonly RecordingDatabaseHelper _db = new();
    private CompanyMembershipStore Store() => new(_db);

    [TestMethod]
    public async Task ChangingARole_KeepsTheLastAdminGuardAndCompanyFilterInTheSameUpdate()
    {
        await Store().ChangeRoleAsync(Guid.NewGuid(), Guid.NewGuid(), AppRoles.Viewer, null);

        var sql = _db.Statements[0].Sql;
        StringAssert.StartsWith(sql, "UPDATE CompanyMembership");
        StringAssert.Contains(sql, "CompanyId = @CompanyId");
        StringAssert.Contains(sql, "EXISTS");
        StringAssert.Contains(sql, "o.Role = 'Admin' AND o.IsActive = 1");
        StringAssert.Contains(sql, "@ExpectedRole IS NULL OR Role = @ExpectedRole");
    }

    [TestMethod]
    public async Task ChangingThePrimaryCompanysRole_AlsoMirrorsTheLegacyUserColumn()
    {
        await Store().ChangeRoleAsync(Guid.NewGuid(), Guid.NewGuid(), AppRoles.Operator, null);

        Assert.IsTrue(_db.Statements.Any(s => s.Sql.Contains("UPDATE `User` SET Role = @Role")));
        Assert.IsTrue(_db.Statements.Any(s => s.Sql.Contains("CompanyId = @CompanyId")));
    }

    [TestMethod]
    public async Task SwitchingOffInOnesOnlyCompany_AlsoSwitchesTheAccountOff()
    {
        await Store().SetActiveAsync(Guid.NewGuid(), Guid.NewGuid(), false);

        Assert.IsTrue(_db.Statements.Any(s => s.Sql.Contains("u.IsActive = 0")));
        Assert.IsTrue(_db.Statements.Any(s => s.Sql.Contains("NOT EXISTS")));
    }

    [TestMethod]
    public async Task SwitchingOn_ReEnablesTheAccountOnlyWhenNoOtherCompanyIsActive()
    {
        await Store().SetActiveAsync(Guid.NewGuid(), Guid.NewGuid(), true);

        Assert.IsTrue(_db.Statements.Any(s => s.Sql.Contains("u.IsActive = 1")));
        Assert.IsTrue(_db.Statements.Any(s => s.Sql.Contains("SecurityStamp")));
    }

    [TestMethod]
    public async Task EnsuringAMembership_IsIdempotent()
    {
        await Store().EnsureAsync(Guid.NewGuid(), Guid.NewGuid(), AppRoles.Admin);

        StringAssert.Contains(_db.LastSql, "ON DUPLICATE KEY UPDATE");
    }

    [TestMethod]
    public async Task ATeamPage_IsPagedAndCountedInSql_ForOneCompany()
    {
        var company = Guid.NewGuid();
        await Store().ListCompanyPageAsync(company, page: 3, pageSize: 20);

        var page = _db.Statements[0];
        StringAssert.Contains(page.Sql, "WHERE m.CompanyId = @CompanyId");
        StringAssert.Contains(page.Sql, "ORDER BY u.Name");
        StringAssert.Contains(page.Sql, "LIMIT @Take OFFSET @Skip");
        var parameters = RecordingDatabaseHelper.ParametersOf(page.Parameters);
        Assert.AreEqual(company, parameters["CompanyId"]);
        Assert.AreEqual(20, parameters["Take"]);
        Assert.AreEqual(40, parameters["Skip"]);
        StringAssert.Contains(_db.Statements[1].Sql, "COUNT(*)");
        StringAssert.Contains(_db.Statements[1].Sql, "m.CompanyId = @CompanyId");
    }

    [TestMethod]
    public async Task ListingForAUser_OnlyReturnsActiveMembershipsOfLiveCompanies()
    {
        await Store().ListForUserAsync(Guid.NewGuid());

        var sql = _db.LastSql;
        StringAssert.Contains(sql, "FROM CompanyMembership m");
        StringAssert.Contains(sql, "c.Deleted = 0");
        StringAssert.Contains(sql, "@IncludeInactive = 1 OR m.IsActive = 1");
    }
}
