using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Database.Providers;
using ShipRight.Modules.Projects;

namespace ShipRight.Tests.Modules.Database;

[TestClass]
public class SqlServerProviderCommandTests
{
    private static DatabaseConfig Config() => new()
    {
        Provider = DbProviderType.SqlServer,
        ContainerName = "shop-sql",
        DatabaseName = "shop",
        RootUser = "sa",
        RootPassword = "Str0ng!Pass",
    };

    [TestMethod]
    public void Backup_CopiesTheFileOutOfTheContainer()
    {
        var command = new SqlServerProvider().BackupCommand(Config()).Replace("{{opId}}", "abc");

        StringAssert.Contains(command, "BACKUP DATABASE [shop]");
        StringAssert.Contains(command, "COMPRESSION");
        StringAssert.Contains(command, "CHECKSUM");
        StringAssert.Contains(command, "docker cp shop-sql:/var/opt/mssql/backup/shipright-backup-abc.bak /tmp/shipright-backup-abc.bak");
        StringAssert.Contains(command, "rm -f /var/opt/mssql/backup/shipright-backup-abc.bak");
    }

    [TestMethod]
    public void Backup_FindsSqlcmdAcrossImages()
    {
        var command = new SqlServerProvider().BackupCommand(Config());

        StringAssert.Contains(command, "command -v sqlcmd");
        StringAssert.Contains(command, "/opt/mssql-tools18/bin/sqlcmd");
        StringAssert.Contains(command, "/opt/mssql-tools/bin/sqlcmd");
        StringAssert.Contains(command, "-C");
    }

    [TestMethod]
    public void Restore_CopiesIn_Restores_AndCleansUp()
    {
        var command = new SqlServerProvider().RestoreCommand(Config(), "/tmp/shipright-restore-op1.bak");

        StringAssert.Contains(command, "docker cp /tmp/shipright-restore-op1.bak shop-sql:/var/opt/mssql/backup/shipright-restore-op1.bak");
        StringAssert.Contains(command, "RESTORE DATABASE [shop]");
        StringAssert.Contains(command, "WITH REPLACE");
        StringAssert.Contains(command, "rm -f /var/opt/mssql/backup/shipright-restore-op1.bak");
        StringAssert.Contains(command, "exit $rc");
    }

    [TestMethod]
    public void Query_CopiesTheScriptInAndRemovesItAfterwards()
    {
        var command = new SqlServerProvider().QueryCommand(Config(), "/tmp/shipright-query-op1.sql");

        StringAssert.Contains(command, "docker cp /tmp/shipright-query-op1.sql shop-sql:/var/opt/mssql/backup/shipright-query-op1.sql");
        StringAssert.Contains(command, "-i /var/opt/mssql/backup/shipright-query-op1.sql");
        StringAssert.Contains(command, "rm -f /var/opt/mssql/backup/shipright-query-op1.sql");
    }

    [TestMethod]
    public void PasswordIsQuotedAndNeverExpandedOnTheHost()
    {
        var command = new SqlServerProvider().BackupCommand(Config());

        StringAssert.Contains(command, "-P \"Str0ng!Pass\"");
    }
}
