using ShipRight.Modules.Projects;

namespace ShipRight.Modules.Database.Providers;

public enum BackupTransfer { StreamStdout, SftpFile }

public interface IDbProvider
{
    string ProviderName    { get; }
    string PasswordEnvVar  { get; }
    string BackupExtension { get; }
    BackupTransfer Transfer { get; }

    string BackupCommand         (DatabaseConfig cfg);
    string BackupFilePath        (DatabaseConfig cfg, string opId);
    string RestoreCommand        (DatabaseConfig cfg, string remoteFilePath);
    string QueryCommand          (DatabaseConfig cfg, string remoteFilePath);
    string CleanupCommand        (string remoteFilePath);
    string ListDatabasesCommand  (DatabaseConfig cfg);
}
