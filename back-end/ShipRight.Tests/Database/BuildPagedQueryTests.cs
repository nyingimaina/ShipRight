using System.Security.Claims;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Builds;
using ShipRight.Shared.Tenancy;
using ShipRight.Tests.Modules.Tenancy;

namespace ShipRight.Tests.Database;

// The MySQL paging builder wraps the query in "select * from (...) as t ... order by StartedAt".
// If the inner select only projects the serialized Data column, MariaDB has no StartedAt in the
// outer scope and the app dies on startup with "Unknown column 'StartedAt' in 'ORDER BY'".
[TestClass]
public class BuildPagedQueryTests
{
    private static void AssertInnerSelectProjectsPageColumn(string sql)
    {
        var innerStart = sql.IndexOf("from (", StringComparison.OrdinalIgnoreCase);
        var innerEnd = sql.IndexOf(") as t", StringComparison.OrdinalIgnoreCase);
        Assert.IsTrue(innerStart >= 0 && innerEnd > innerStart, $"Unexpected paged SQL: {sql}");
        var innerSelect = sql[innerStart..innerEnd];
        StringAssert.Contains(
            innerSelect,
            "StartedAt",
            "The derived table must select StartedAt because the outer ORDER BY uses it.");
    }

    [TestMethod]
    public async Task MariaDbBuildStore_QueryAsync_ProjectsPageColumnInsideDerivedTable()
    {
        var db = new RecordingDatabaseHelper();
        var store = new MariaDbBuildStore(db);

        await store.QueryAsync(null, null, null, null, null, 1, 10);

        AssertInnerSelectProjectsPageColumn(db.LastSql);
    }

    [TestMethod]
    public async Task CompanyScopedBuildStore_QueryAsync_ProjectsPageColumnInsideDerivedTable()
    {
        var company = Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000a");
        CompanyContext.Set(company);
        try
        {
            var db = new RecordingDatabaseHelper();
            var scope = new CompanyScope(new CompanyContext(), db);
            var store = new CompanyScopedBuildStore(db, scope);

            await store.QueryAsync(null, null, null, null, null, 1, 10);

            AssertInnerSelectProjectsPageColumn(db.LastSql);
        }
        finally
        {
            CompanyContext.Set(null);
        }
    }
}
