using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Database;

namespace ShipRight.Tests.Database;

// R11, R12: the upgrade of existing data is numbered, reversible, safe to run again, and never loses an account.
[TestClass]
public class MigrationScriptsTests
{
    private static string ServerFolder()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "back-end", "ShipRight.Server", "Database")))
            dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? throw new DirectoryNotFoundException("repo root"), "back-end", "ShipRight.Server", "Database");
    }

    private static string[] MigrationFiles(string suffix) =>
        Directory.GetFiles(Path.Combine(ServerFolder(), "Migrations"), $"*{suffix}").Select(Path.GetFileName).OrderBy(n => n).ToArray()!;

    [TestMethod]
    public void EveryUpScript_HasADownScript_WithTheSameNumberAndName()
    {
        // DB-001
        var ups = MigrationFiles(".up.sql").Select(n => n.Replace(".up.sql", "")).ToArray();
        var downs = MigrationFiles(".down.sql").Select(n => n.Replace(".down.sql", "")).ToArray();

        Assert.IsTrue(ups.Length >= 2);
        CollectionAssert.AreEqual(ups, downs);
        foreach (var name in ups) Assert.IsTrue(Regex.IsMatch(name, @"^\d{4}_[a-z_]+$"), name);
    }

    [TestMethod]
    public void UpScripts_AreEmbedded_InNumericOrder()
    {
        var scripts = MigrationRunner.LoadUpScripts(typeof(MigrationRunner).Assembly);

        CollectionAssert.AreEqual(
            MigrationFiles(".up.sql").Select(n => n.Replace(".up.sql", "")).ToArray(),
            scripts.Select(s => s.Id).ToArray());
        Assert.IsTrue(scripts.All(s => s.Sql.Length > 0));
    }

    [TestMethod]
    public void UpScripts_CanBeRunAgain_EveryStatementIsGuarded()
    {
        // R11: upgrading twice, or restarting half-way, leaves the data intact.
        foreach (var script in MigrationRunner.LoadUpScripts(typeof(MigrationRunner).Assembly))
        {
            var sql = Regex.Replace(script.Sql, @"--.*", "");
            foreach (var statement in sql.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var s = Regex.Replace(statement, @"\s+", " ");
                if (s.StartsWith("CREATE TABLE", StringComparison.OrdinalIgnoreCase)) StringAssert.Contains(s, "IF NOT EXISTS", script.Id);
                if (s.StartsWith("DROP TABLE", StringComparison.OrdinalIgnoreCase)) StringAssert.Contains(s, "IF EXISTS", script.Id);
                if (s.StartsWith("CREATE UNIQUE INDEX", StringComparison.OrdinalIgnoreCase) || s.StartsWith("CREATE INDEX", StringComparison.OrdinalIgnoreCase))
                    StringAssert.Contains(s, "IF NOT EXISTS", script.Id);
                if (s.StartsWith("ALTER TABLE", StringComparison.OrdinalIgnoreCase) && Regex.IsMatch(s, "ADD COLUMN"))
                    Assert.IsFalse(Regex.IsMatch(s, @"ADD COLUMN (?!IF NOT EXISTS)"), $"{script.Id}: {s}");
                if (s.StartsWith("ALTER TABLE", StringComparison.OrdinalIgnoreCase) && Regex.IsMatch(s, "DROP COLUMN"))
                    Assert.IsFalse(Regex.IsMatch(s, @"DROP COLUMN (?!IF EXISTS)"), $"{script.Id}: {s}");
            }
        }
    }

    [TestMethod]
    public void IdentityUpgrade_DeletesNoAccountOrCompany()
    {
        // R12: accounts and companies are only ever added to or updated.
        var up = MigrationRunner.LoadUpScripts(typeof(MigrationRunner).Assembly).Single(s => s.Id == "0001_identity").Sql;

        Assert.IsFalse(Regex.IsMatch(up, @"DELETE\s+FROM", RegexOptions.IgnoreCase));
        Assert.IsFalse(Regex.IsMatch(up, @"DROP\s+TABLE", RegexOptions.IgnoreCase));
        Assert.IsFalse(Regex.IsMatch(up, @"TRUNCATE", RegexOptions.IgnoreCase));
        Assert.IsFalse(Regex.IsMatch(up, @"DROP\s+COLUMN", RegexOptions.IgnoreCase), "old columns go in 0002, after the data is moved");
    }

    [TestMethod]
    public void IdentityUpgrade_MakesEveryExistingAccountAnAdmin_AndKeepsOldPasswordHashes()
    {
        // R1: nobody loses the access they had; the hash column is only made nullable, never rewritten.
        var up = MigrationRunner.LoadUpScripts(typeof(MigrationRunner).Assembly).Single(s => s.Id == "0001_identity").Sql;

        StringAssert.Contains(up, "SET Role = 'Admin'");
        StringAssert.Contains(up, "MODIFY PasswordHash VARCHAR(255) NULL");
        Assert.IsFalse(Regex.IsMatch(up, @"SET\s+PasswordHash", RegexOptions.IgnoreCase));
        StringAssert.Contains(up, "UPPER(Email)");
    }

    [TestMethod]
    public void IdentityUpgrade_GivesOrphanedCloudRowsToTheOldestCompany()
    {
        // R4, R11
        var up = MigrationRunner.LoadUpScripts(typeof(MigrationRunner).Assembly).Single(s => s.Id == "0001_identity").Sql;

        foreach (var table in new[] { "Project", "Server", "Build", "DockerRegistryResource", "ScriptResource", "CredentialResource", "PipelineResource", "AwsProfileResource", "BackupHistory", "WatchBranchHistory", "SshKey" })
            Assert.IsTrue(Regex.IsMatch(up, $@"UPDATE {table} \w+ SET"), table);
    }

    [TestMethod]
    public void IdentityTables_AreIndexedForUniquenessAndLookup()
    {
        // DB-002
        var up = MigrationRunner.LoadUpScripts(typeof(MigrationRunner).Assembly).Single(s => s.Id == "0001_identity").Sql;

        StringAssert.Contains(up, "CREATE UNIQUE INDEX IF NOT EXISTS uq_user_normalized_email");
        StringAssert.Contains(up, "idx_user_company_name ON `User` (CompanyId, Name)");
        StringAssert.Contains(up, "PRIMARY KEY (Provider, ProviderKey)");
        StringAssert.Contains(up, "idx_external_login_user");
    }

    [TestMethod]
    public void LegacyPieces_AreDroppedOnlyInTheSecondMigration()
    {
        // R12
        var scripts = MigrationRunner.LoadUpScripts(typeof(MigrationRunner).Assembly);
        var drop = scripts.Single(s => s.Id == "0002_drop_legacy_auth").Sql;

        StringAssert.Contains(drop, "DROP COLUMN IF EXISTS IsAdmin");
        StringAssert.Contains(drop, "DROP TABLE IF EXISTS RefreshToken");
        StringAssert.Contains(drop, "DROP TABLE IF EXISTS ResetToken");
        Assert.IsFalse(Regex.IsMatch(drop, @"DROP\s+TABLE\s+(IF EXISTS\s+)?(User|Company)\b", RegexOptions.IgnoreCase));
    }

    [TestMethod]
    public void DownScripts_PutTheOldShapeBack()
    {
        // DB-001
        var folder = Path.Combine(ServerFolder(), "Migrations");
        var down1 = File.ReadAllText(Path.Combine(folder, "0001_identity.down.sql"));
        var down2 = File.ReadAllText(Path.Combine(folder, "0002_drop_legacy_auth.down.sql"));

        StringAssert.Contains(down1, "DROP TABLE IF EXISTS ExternalLogin");
        StringAssert.Contains(down1, "DROP COLUMN IF EXISTS Role");
        StringAssert.Contains(down2, "ADD COLUMN IF NOT EXISTS IsAdmin");
        StringAssert.Contains(down2, "CREATE TABLE IF NOT EXISTS RefreshToken");
        StringAssert.Contains(down2, "CREATE TABLE IF NOT EXISTS ResetToken");
    }

    [TestMethod]
    public void FreshInstallSchema_HasTheFinalShape_AndMarksEveryMigrationDone()
    {
        // R12: schema.sql for new databases matches the upgraded shape, so the runner has nothing to do.
        var schema = File.ReadAllText(Path.Combine(ServerFolder(), "schema.sql"));

        foreach (var column in new[] { "NormalizedEmail", "Role", "IsActive", "MustChangePassword", "SecurityStamp", "LockoutEnd", "AccessFailedCount" })
            StringAssert.Contains(schema, column);
        StringAssert.Contains(schema, "CREATE TABLE ExternalLogin");
        StringAssert.Contains(schema, "CREATE TABLE SchemaMigration");
        Assert.IsFalse(schema.Contains("IsAdmin"));
        Assert.IsFalse(schema.Contains("CREATE TABLE RefreshToken"));
        Assert.IsFalse(schema.Contains("CREATE TABLE ResetToken"));
        foreach (var name in MigrationFiles(".up.sql").Select(n => n.Replace(".up.sql", "")))
            StringAssert.Contains(schema, $"'{name}'");
    }
}
