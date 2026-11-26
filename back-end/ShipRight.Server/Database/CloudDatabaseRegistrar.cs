using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Rocket.Libraries.DatabaseIntegrator;

namespace ShipRight.Database;

public static class CloudDatabaseRegistrar
{
    public const string DefaultConnectionString =
        "Server=localhost;Port=3306;Database=shipright;User Id=root;Password=;Charset=utf8;ConvertZeroDateTime=True;";

    public static void Register(IServiceCollection services, IConfiguration config, string? envConnectionString)
    {
        var connectionString = envConnectionString
            ?? config.GetConnectionString("DefaultConnection")
            ?? DefaultConnectionString;

        config["DatabaseConnectionSettings:ConnectionString"] = connectionString;
        services.Configure<DatabaseConnectionSettings>(config.GetSection("DatabaseConnectionSettings"));
        services.Configure<DatabaseSettings>(config.GetSection("DatabaseConnectionSettings"));
        services.AddSingleton<IConnectionProvider, DatabaseConnectionProvider>();
        services.AddSingleton<IDatabaseHelper<Guid>>(sp =>
            // A fresh integrator per reconnect: the library holds one connection forever and never reopens it.
            new SerializedDatabaseHelper<Guid>(() =>
                ActivatorUtilities.CreateInstance<DatabaseHelper<Guid>>(sp)));
        services.AddSingleton<MigrationRunner>();
    }
}
