using ShipRight.Modules.Projects;

namespace ShipRight.Modules.Database.Providers;

public interface IDbProviderResolver
{
    IDbProvider Resolve(DbProviderType providerType);
}

public class DbProviderResolver : IDbProviderResolver
{
    private readonly MariaDbProvider _mariaDb;
    private readonly SqlServerProvider _sqlServer;

    public DbProviderResolver(MariaDbProvider mariaDb, SqlServerProvider sqlServer)
    {
        _mariaDb   = mariaDb;
        _sqlServer = sqlServer;
    }

    public IDbProvider Resolve(DbProviderType providerType) => providerType switch
    {
        DbProviderType.MariaDb    => _mariaDb,
        DbProviderType.SqlServer  => _sqlServer,
        _ => throw new ArgumentOutOfRangeException(nameof(providerType), providerType, null)
    };
}
