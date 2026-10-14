using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Rocket.Libraries.DatabaseIntegrator;
using ShipRight.Database;
using ShipRight.Tests.Modules.Auth;
using ShipRight.Tests.Modules.Identity;

namespace ShipRight.Tests.Database;

// R1, R4, R11, R12: the upgrade of a database that already holds accounts, run against a real MariaDB.
// Needs SHIPRIGHT_TEST_DB_CONNECTION (see MariaDbTestDatabase); without it these are inconclusive.
[TestClass]
public class MigrationUpgradeTests
{
    private static readonly Guid OldCompany = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid NewCompany = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static MigrationRunner RunnerFor(MariaDbTestDatabase db) =>
        new(Options.Create(new DatabaseConnectionSettings { ConnectionString = db.ConnectionString }), NullLogger<MigrationRunner>.Instance);

    private static MariaDbTestDatabase LegacyDatabaseWithAccounts(string legacyHash)
    {
        var db = MariaDbTestDatabase.CreateLegacyOrSkip();
        db.Execute("INSERT INTO Company (Id, Name, Slug, Created) VALUES (@c, 'Old Co', 'old-co', '2024-01-01 00:00:00')", true, ("c", OldCompany.ToString()));
        db.Execute("INSERT INTO Company (Id, Name, Slug, Created) VALUES (@c, 'New Co', 'new-co', '2025-01-01 00:00:00')", true, ("c", NewCompany.ToString()));
        db.Execute("INSERT INTO `User` (Id, CompanyId, Email, PasswordHash, Name, IsAdmin) VALUES (UUID(), @c, 'Owner@Example.test', @h, 'Owner', 1)",
            true, ("c", OldCompany.ToString()), ("h", legacyHash));
        db.Execute("INSERT INTO `User` (Id, CompanyId, Email, PasswordHash, Name, IsAdmin) VALUES (UUID(), @c, 'staff@example.test', @h, 'Staff', 0)",
            true, ("c", NewCompany.ToString()), ("h", legacyHash));
        // A project written by the old cloud store with the empty company id (foreign keys off for this one row).
        db.Execute("SET FOREIGN_KEY_CHECKS = 0; INSERT INTO Project (Id, CompanyId, Name, Data) VALUES (UUID(), '00000000-0000-0000-0000-000000000000', 'orphan', '{}'); SET FOREIGN_KEY_CHECKS = 1;");
        db.Execute("INSERT INTO Project (Id, CompanyId, Name, Data) VALUES (UUID(), @c, 'owned', '{}')", true, ("c", NewCompany.ToString()));
        return db;
    }

    [TestMethod]
    public async Task Upgrade_KeepsEveryAccountAndCompany_MakesThemAdmins_AndKeepsTheOldPasswordHash()
    {
        // R1, R12
        var legacyHash = AuthCharacterizationTests.BuildLegacyHash("old-password-1");
        using var db = LegacyDatabaseWithAccounts(legacyHash);

        await RunnerFor(db).RunAsync();

        Assert.AreEqual(2L, db.Scalar<long>("SELECT COUNT(*) FROM `User`"));
        Assert.AreEqual(2L, db.Scalar<long>("SELECT COUNT(*) FROM Company"));
        Assert.AreEqual(2L, db.Scalar<long>("SELECT COUNT(*) FROM `User` WHERE Role = 'Admin' AND IsActive = 1"), "a non-admin account keeps the access it had");
        Assert.AreEqual(2L, db.Scalar<long>("SELECT COUNT(*) FROM `User` WHERE PasswordHash = @h", ("h", legacyHash)));
        Assert.AreEqual("OWNER@EXAMPLE.TEST", db.Scalar<string>("SELECT NormalizedEmail FROM `User` WHERE Name = 'Owner'"));
        Assert.AreEqual(0L, db.Scalar<long>("SELECT COUNT(*) FROM `User` WHERE SecurityStamp IS NULL OR SecurityStamp = ''"));
    }

    [TestMethod]
    public async Task Upgrade_GivesOrphanedRowsToTheOldestCompany_AndLosesNoProject()
    {
        // R4, R11
        using var db = LegacyDatabaseWithAccounts(AuthCharacterizationTests.BuildLegacyHash("x-password-1"));

        await RunnerFor(db).RunAsync();

        Assert.AreEqual(2L, db.Scalar<long>("SELECT COUNT(*) FROM Project"));
        Assert.AreEqual(0L, db.Scalar<long>("SELECT COUNT(*) FROM Project WHERE CompanyId = '00000000-0000-0000-0000-000000000000'"));
        Assert.AreEqual(OldCompany.ToString(), db.Scalar<string>("SELECT CompanyId FROM Project WHERE Name = 'orphan'"));
        Assert.AreEqual(NewCompany.ToString(), db.Scalar<string>("SELECT CompanyId FROM Project WHERE Name = 'owned'"));
    }

    [TestMethod]
    public async Task Upgrade_RunTwice_LeavesTheDataIntact()
    {
        // R11: upgrading twice, or restarting during the upgrade.
        using var db = LegacyDatabaseWithAccounts(AuthCharacterizationTests.BuildLegacyHash("x-password-1"));
        var runner = RunnerFor(db);

        await runner.RunAsync();
        var after = db.Scalar<string>("SELECT GROUP_CONCAT(Id ORDER BY Id) FROM `User`");
        await runner.RunAsync();

        Assert.AreEqual(after, db.Scalar<string>("SELECT GROUP_CONCAT(Id ORDER BY Id) FROM `User`"));
        Assert.AreEqual(2L, db.Scalar<long>("SELECT COUNT(*) FROM SchemaMigration"));
        Assert.AreEqual(2L, db.Scalar<long>("SELECT COUNT(*) FROM `User` WHERE Role = 'Admin'"));
    }

    [TestMethod]
    public async Task Upgrade_RerunningTheFirstScriptByHand_IsHarmless()
    {
        // R11: a crash between a script and its record just runs the script again.
        using var db = LegacyDatabaseWithAccounts(AuthCharacterizationTests.BuildLegacyHash("x-password-1"));
        await RunnerFor(db).RunAsync();
        var first = MigrationRunner.LoadUpScripts(typeof(MigrationRunner).Assembly).Single(s => s.Id == "0001_identity").Sql;

        db.RunScript(first);

        Assert.AreEqual(2L, db.Scalar<long>("SELECT COUNT(*) FROM `User`"));
    }

    [TestMethod]
    public async Task Upgrade_DropsTheOldTokenTablesAndColumns_ButAddsIdentityTables()
    {
        // R12
        using var db = LegacyDatabaseWithAccounts(AuthCharacterizationTests.BuildLegacyHash("x-password-1"));

        await RunnerFor(db).RunAsync();

        long Columns(string table, string column) => db.Scalar<long>(
            "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = @t AND column_name = @c", ("t", table), ("c", column));
        long Tables(string table) => db.Scalar<long>(
            "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = @t", ("t", table));

        Assert.AreEqual(0L, Columns("User", "IsAdmin"));
        Assert.AreEqual(0L, Columns("User", "TokenVersion"));
        Assert.AreEqual(0L, Tables("RefreshToken"));
        Assert.AreEqual(0L, Tables("ResetToken"));
        Assert.AreEqual(1L, Tables("ExternalLogin"));
        Assert.AreEqual(1L, Columns("User", "Role"));
    }

    [TestMethod]
    public async Task Upgrade_EnforcesUniqueEmailsRegardlessOfCapitals()
    {
        // R2, DB-002
        using var db = LegacyDatabaseWithAccounts(AuthCharacterizationTests.BuildLegacyHash("x-password-1"));
        await RunnerFor(db).RunAsync();

        Assert.ThrowsException<MySql.Data.MySqlClient.MySqlException>(() =>
            db.Execute("INSERT INTO `User` (Id, CompanyId, Email, NormalizedEmail, Name, Role, SecurityStamp) VALUES (UUID(), @c, 'other@example.test', 'OWNER@EXAMPLE.TEST', 'Dup', 'Viewer', 's')",
                true, ("c", OldCompany.ToString())));
    }

    [TestMethod]
    public void FreshInstall_FromSchemaSql_NeedsNoMigration()
    {
        // R12
        using var db = MariaDbTestDatabase.CreateOrSkip();

        Assert.AreEqual(2L, db.Scalar<long>("SELECT COUNT(*) FROM SchemaMigration"));
        Assert.AreEqual(1L, db.Scalar<long>("SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'User' AND column_name = 'Role'"));
    }

    [TestMethod]
    public async Task DownScripts_RestoreTheOldShape_WithoutLosingAccounts()
    {
        // DB-001, R12
        using var db = LegacyDatabaseWithAccounts(AuthCharacterizationTests.BuildLegacyHash("x-password-1"));
        await RunnerFor(db).RunAsync();
        var folder = Path.Combine(Path.GetDirectoryName(MariaDbTestDatabase.SchemaPath())!, "Migrations");

        db.RunScript(File.ReadAllText(Path.Combine(folder, "0002_drop_legacy_auth.down.sql")));
        db.RunScript(File.ReadAllText(Path.Combine(folder, "0001_identity.down.sql")));

        Assert.AreEqual(2L, db.Scalar<long>("SELECT COUNT(*) FROM `User`"));
        Assert.AreEqual(1L, db.Scalar<long>("SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'User' AND column_name = 'IsAdmin'"));
        Assert.AreEqual(2L, db.Scalar<long>("SELECT COUNT(*) FROM `User` WHERE IsAdmin = 1"));
    }
}
