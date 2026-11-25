using ShipRight.Modules.Projects;

namespace ShipRight.Modules.Database.Providers;

public class SqlServerProvider : IDbProvider
{
    public string ProviderName    => "SQL Server";
    public string PasswordEnvVar  => "SA_PASSWORD";
    public string BackupExtension => ".bak";
    public BackupTransfer Transfer => BackupTransfer.SftpFile;

    // The .bak is written inside the container, so every path that crosses the boundary needs a docker cp.
    private const string ContainerBackupDir = "/var/opt/mssql/backup";

    private string Pw(DatabaseConfig cfg) =>
        string.IsNullOrEmpty(cfg.RootPassword) ? "$" + PasswordEnvVar : cfg.RootPassword;

    // Finds sqlcmd on PATH, then the tools18 path used by current images, then the legacy path.
    private const string ResolveSqlcmd =
        "SQLCMD=$(command -v sqlcmd 2>/dev/null || true); " +
        "[ -n \"$SQLCMD\" ] || SQLCMD=/opt/mssql-tools18/bin/sqlcmd; " +
        "[ -x \"$SQLCMD\" ] || SQLCMD=/opt/mssql-tools/bin/sqlcmd; ";

    private static string Sh(string value) => "'" + value.Replace("'", "'\\''") + "'";

    private string Sqlcmd(DatabaseConfig cfg, string query, string flags = "") =>
        "docker exec " + cfg.ContainerName + " sh -c " + Sh(
            ResolveSqlcmd +
            $"\"$SQLCMD\" -S localhost -U {cfg.RootUser} -P \"{Pw(cfg)}\" -C -b {flags}-Q \"{query}\"");

    public string BackupCommand(DatabaseConfig cfg)
    {
        var containerPath = $"{ContainerBackupDir}/shipright-backup-{{{{opId}}}}.bak";
        var hostPath = BackupFilePath(cfg, "{{opId}}");
        var backingUp = Sqlcmd(cfg,
            $"BACKUP DATABASE [{cfg.DatabaseName}] TO DISK = N'{containerPath}' " +
            "WITH NOFORMAT, INIT, COMPRESSION, CHECKSUM, STATS = 10");

        return $"{backingUp} && docker cp {cfg.ContainerName}:{containerPath} {hostPath} && " +
               $"docker exec {cfg.ContainerName} rm -f {containerPath}";
    }

    public string BackupFilePath(DatabaseConfig cfg, string opId) =>
        $"/tmp/shipright-backup-{opId}{BackupExtension}";

    public string RestoreCommand(DatabaseConfig cfg, string remoteFilePath)
    {
        var name = global::System.IO.Path.GetFileName(remoteFilePath);
        var containerPath = $"{ContainerBackupDir}/{name}";
        var restore = Sqlcmd(cfg,
            $"RESTORE DATABASE [{cfg.DatabaseName}] FROM DISK = N'{containerPath}' WITH REPLACE");

        return $"docker cp {remoteFilePath} {cfg.ContainerName}:{containerPath} && {restore} ; " +
               $"rc=$?; docker exec {cfg.ContainerName} rm -f {containerPath}; exit $rc";
    }

    public string QueryCommand(DatabaseConfig cfg, string remoteFilePath)
    {
        var name = global::System.IO.Path.GetFileName(remoteFilePath);
        var containerPath = $"{ContainerBackupDir}/{name}";
        var query = "docker exec " + cfg.ContainerName + " sh -c " + Sh(
            ResolveSqlcmd +
            $"\"$SQLCMD\" -S localhost -U {cfg.RootUser} -P \"{Pw(cfg)}\" -C -b -s $'\\t' -W -i {containerPath}");

        return $"docker cp {remoteFilePath} {cfg.ContainerName}:{containerPath} && {query} ; " +
               $"rc=$?; docker exec {cfg.ContainerName} rm -f {containerPath}; exit $rc";
    }

    public string CleanupCommand(string remoteFilePath) => $"rm -f {remoteFilePath}";

    public string ListDatabasesCommand(DatabaseConfig cfg) =>
        Sqlcmd(cfg, "SELECT name FROM sys.databases ORDER BY name", "-h -1 -W ");
}
