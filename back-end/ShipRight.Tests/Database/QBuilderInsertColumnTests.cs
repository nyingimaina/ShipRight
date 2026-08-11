using System.Text.RegularExpressions;
using Jattac.Libraries.QBuilder;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Auth.Models;

namespace ShipRight.Tests.Database;

[TestClass]
public class QBuilderInsertColumnTests
{
    [TestMethod]
    public void CompanyInsert_DoesNotReferenceHasNoIdColumn()
    {
        var company = new Company
        {
            Id = Guid.NewGuid(),
            Name = "Default",
            Slug = "default",
            Created = DateTime.UtcNow,
            Modified = DateTime.UtcNow,
        };

        using var qBuilder = new QBuilder(parameterize: true);
        var built = qBuilder
            .UseTableBoundInsert<Company>()
            .FromObject(company)
            .BuildWithParameters();

        StringAssert.DoesNotMatch(built.ParameterizedSql, new Regex(@"HasNoId", RegexOptions.IgnoreCase));
    }

    [TestMethod]
    public void UserInsert_DoesNotReferenceHasNoIdColumn()
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            CompanyId = Guid.NewGuid(),
            Email = "admin@example.com",
            PasswordHash = "hash",
            Name = "Admin",
            IsAdmin = true,
            TokenVersion = 1,
            Created = DateTime.UtcNow,
            Modified = DateTime.UtcNow,
        };

        using var qBuilder = new QBuilder(parameterize: true);
        var built = qBuilder
            .UseTableBoundInsert<User>()
            .FromObject(user)
            .BuildWithParameters();

        StringAssert.DoesNotMatch(built.ParameterizedSql, new Regex(@"HasNoId", RegexOptions.IgnoreCase));
    }
}
