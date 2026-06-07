using ShipRight.Modules.Projects;

namespace ShipRight.Modules.Database.Providers;

public class MariaDbProvider : IDbProvider
{
    public string ProviderName    => "MariaDB";
    public string PasswordEnvVar  => "MYSQL_ROOT_PASSWORD";
    public string BackupExtension => ".sql";
    public BackupTransfer Transfer => BackupTransfer.StreamStdout;

    public string BackupCommand(DatabaseConfig cfg)
    {
        var pw = "$" + PasswordEnvVar;
        return $"docker exec {cfg.ContainerName} sh -c " +
               $"'exec mysqldump -u{cfg.RootUser} -p\"{pw}\" --routines --single-transaction {cfg.DatabaseName}'";
    }

    public string BackupFilePath(DatabaseConfig cfg, string opId) =>
        $"/tmp/shipright-backup-{opId}{BackupExtension}";

    public string RestoreCommand(DatabaseConfig cfg, string remoteFilePath)
    {
        var pw = "$" + PasswordEnvVar;
        return $"docker exec -i {cfg.ContainerName} sh -c " +
               $"'exec mysql -u{cfg.RootUser} -p\"{pw}\" {cfg.DatabaseName}' < {remoteFilePath}";
    }

    public string QueryCommand(DatabaseConfig cfg, string remoteFilePath)
    {
        var pw = "$" + PasswordEnvVar;
        return $"docker exec -i {cfg.ContainerName} sh -c " +
               $"'exec mysql -u{cfg.RootUser} -p\"{pw}\" {cfg.DatabaseName}' < {remoteFilePath}";
    }

    public string CleanupCommand(string remoteFilePath) => $"rm -f {remoteFilePath}";

    public string ListDatabasesCommand(DatabaseConfig cfg)
    {
        var pw = "$" + PasswordEnvVar;
        return $"docker exec {cfg.ContainerName} sh -c " +
               $"'exec mysql -u{cfg.RootUser} -p\"{pw}\" -e \"SHOW DATABASES;\"'";
    }
}
