using System.Data;
using Microsoft.Extensions.Options;
using MySql.Data.MySqlClient;
using Rocket.Libraries.DatabaseIntegrator;

namespace ShipRight.Database;

public class DatabaseConnectionProvider : IConnectionProvider
{
    private readonly string _connectionString;

    public DatabaseConnectionProvider(IOptions<DatabaseSettings> databaseOptions)
    {
        _connectionString = databaseOptions.Value.ConnectionString;
    }

    public IDbConnection Get(string connectionString)
    {
        return new MySqlConnection(connectionString);
    }
}
