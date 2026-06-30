using ShipRight.Modules.Projects;

namespace ShipRight.Modules.Database.Providers;

public class MariaDbProvider : IDbProvider
{
    public string ProviderName    => "MariaDB";
    public string PasswordEnvVar  => "MYSQL_ROOT_PASSWORD";
    public string BackupExtension => ".sql";
    public BackupTransfer Transfer => BackupTransfer.StreamStdout;

    private string Pw(DatabaseConfig cfg) =>
        string.IsNullOrEmpty(cfg.RootPassword) ? "$" + PasswordEnvVar : cfg.RootPassword;

    // Resolves mysql/mysqldump binary name — MariaDB 11+ renamed them to mariadb/mariadb-dump
    private const string MySqlBin  = "$(command -v mysql       2>/dev/null || command -v mariadb      2>/dev/null)";
    private const string DumpBin   = "$(command -v mysqldump   2>/dev/null || command -v mariadb-dump 2>/dev/null)";

    public string BackupCommand(DatabaseConfig cfg)
    {
        var pw = Pw(cfg);
        return $"docker exec {cfg.ContainerName} sh -c " +
               $"'exec {DumpBin} -u{cfg.RootUser} -p\"{pw}\" --routines --single-transaction {cfg.DatabaseName}'";
    }

    public string BackupFilePath(DatabaseConfig cfg, string opId) =>
        $"/tmp/shipright-backup-{opId}{BackupExtension}";

    public string RestoreCommand(DatabaseConfig cfg, string remoteFilePath)
    {
        var pw = Pw(cfg);
        return $"docker exec -i {cfg.ContainerName} sh -c " +
               $"'exec {MySqlBin} -u{cfg.RootUser} -p\"{pw}\" {cfg.DatabaseName}' < {remoteFilePath}";
    }

    public string QueryCommand(DatabaseConfig cfg, string remoteFilePath)
    {
        var pw = Pw(cfg);
        return $"docker exec -i {cfg.ContainerName} sh -c " +
               $"'exec {MySqlBin} -u{cfg.RootUser} -p\"{pw}\" --batch --column-names {cfg.DatabaseName}' < {remoteFilePath}";
    }

    public string CleanupCommand(string remoteFilePath) => $"rm -f {remoteFilePath}";

    public string ListDatabasesCommand(DatabaseConfig cfg)
    {
        var pw = Pw(cfg);
        return $"docker exec {cfg.ContainerName} sh -c " +
               $"'exec {MySqlBin} -u{cfg.RootUser} -p\"{pw}\" -e \"SHOW DATABASES;\"'";
    }
}
