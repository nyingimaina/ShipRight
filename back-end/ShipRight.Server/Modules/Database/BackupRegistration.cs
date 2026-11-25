using Microsoft.Extensions.DependencyInjection;
using ShipRight.Modules.Database;
using ShipRight.Shared.Store;

namespace ShipRight.Modules.Database;

/// <summary>Registers the backup archive: where files live, which keys encrypt them, and how they are written.</summary>
public static class BackupRegistration
{
    public static IServiceCollection AddBackupArchive(this IServiceCollection services, string dataDir)
    {
        services.AddSingleton(_ => BackupStorage.Resolve(dataDir));
        services.AddSingleton(_ => new BackupKeyring(dataDir));
        services.AddSingleton<BackupRunTelemetry>();
        return services;
    }
}
