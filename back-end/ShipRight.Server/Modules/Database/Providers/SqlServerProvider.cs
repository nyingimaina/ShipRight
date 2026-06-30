using ShipRight.Modules.Projects;

namespace ShipRight.Modules.Database.Providers;

public class SqlServerProvider : IDbProvider
{
    public string ProviderName    => "SQL Server";
    public string PasswordEnvVar  => "SA_PASSWORD";
    public string BackupExtension => ".bak";
    public BackupTransfer Transfer => BackupTransfer.SftpFile;

    private string Pw(DatabaseConfig cfg) =>
        string.IsNullOrEmpty(cfg.RootPassword) ? "$" + PasswordEnvVar : cfg.RootPassword;

    public string BackupCommand(DatabaseConfig cfg)
    {
        var pw = Pw(cfg);
        var remotePath = BackupFilePath(cfg, "{{opId}}");
        return $"docker exec {cfg.ContainerName} /opt/mssql-tools/bin/sqlcmd " +
               $"-S localhost -U {cfg.RootUser} -P \"{pw}\" " +
               $"-Q \"BACKUP DATABASE [{cfg.DatabaseName}] TO DISK = N'{remotePath}' WITH NOFORMAT, NOINIT\"";
    }

    public string BackupFilePath(DatabaseConfig cfg, string opId) =>
        $"/tmp/shipright-backup-{opId}{BackupExtension}";

    public string RestoreCommand(DatabaseConfig cfg, string remoteFilePath)
    {
        var pw = Pw(cfg);
        return $"docker exec {cfg.ContainerName} /opt/mssql-tools/bin/sqlcmd " +
               $"-S localhost -U {cfg.RootUser} -P \"{pw}\" " +
               $"-Q \"RESTORE DATABASE [{cfg.DatabaseName}] FROM DISK = N'{remoteFilePath}' WITH REPLACE\"";
    }

    public string QueryCommand(DatabaseConfig cfg, string remoteFilePath)
    {
        var pw = Pw(cfg);
        return $"docker exec {cfg.ContainerName} /opt/mssql-tools/bin/sqlcmd " +
               $"-S localhost -U {cfg.RootUser} -P \"{pw}\" -s $'\\t' -W -i {remoteFilePath}";
    }

    public string CleanupCommand(string remoteFilePath) => $"rm -f {remoteFilePath}";

    public string ListDatabasesCommand(DatabaseConfig cfg)
    {
        var pw = Pw(cfg);
        return $"docker exec {cfg.ContainerName} /opt/mssql-tools/bin/sqlcmd " +
               $"-S localhost -U {cfg.RootUser} -P \"{pw}\" " +
               $"-Q \"SELECT name FROM sys.databases ORDER BY name\"";
    }
}
