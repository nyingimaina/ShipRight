using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MySql.Data.MySqlClient;
using Rocket.Libraries.DatabaseIntegrator;

namespace ShipRight.Database;

// Applies the numbered NNNN_name.up.sql scripts that have not run yet, in order, once each.
// Each script is written to be safe to run again, so a restart half-way through an upgrade just carries on.
// A database lock keeps two app instances from migrating at the same moment.
public class MigrationRunner(IOptions<DatabaseConnectionSettings> settings, ILogger<MigrationRunner> logger)
{
    internal const string ResourcePrefix = "Migrations/";
    private const string LockName = "shipright-migrations";

    private const string CreateHistory =
        "CREATE TABLE IF NOT EXISTS SchemaMigration (Id VARCHAR(100) NOT NULL PRIMARY KEY, " +
        "AppliedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP) ENGINE=InnoDB";

    public virtual async Task RunAsync(CancellationToken ct = default)
    {
        await using var connection = new MySqlConnection(settings.Value.ConnectionString);
        await connection.OpenAsync(ct);

        await ExecuteAsync(connection, "SELECT GET_LOCK(@name, 120)", ct, ("name", LockName));
        try
        {
            await ExecuteAsync(connection, CreateHistory, ct);
            var applied = await AppliedAsync(connection, ct);

            foreach (var script in LoadUpScripts(typeof(MigrationRunner).Assembly))
            {
                if (applied.Contains(script.Id)) continue;

                logger.LogInformation("Applying database migration {Migration}", script.Id);
                await ExecuteAsync(connection, script.Sql, ct);
                await ExecuteAsync(connection,
                    "INSERT IGNORE INTO SchemaMigration (Id) VALUES (@id)", ct, ("id", script.Id));
            }
        }
        finally
        {
            await ExecuteAsync(connection, "SELECT RELEASE_LOCK(@name)", CancellationToken.None, ("name", LockName));
        }
    }

    // Embedded scripts named Migrations/NNNN_name.up.sql, in numeric order.
    internal static IReadOnlyList<(string Id, string Sql)> LoadUpScripts(Assembly assembly) =>
        assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(".up.sql", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .Select(n =>
            {
                using var stream = assembly.GetManifestResourceStream(n)!;
                using var reader = new StreamReader(stream);
                return (n[ResourcePrefix.Length..^".up.sql".Length], reader.ReadToEnd());
            })
            .ToList();

    private static async Task<HashSet<string>> AppliedAsync(MySqlConnection connection, CancellationToken ct)
    {
        await using var command = new MySqlCommand("SELECT Id FROM SchemaMigration", connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(ct)) ids.Add(reader.GetString(0));
        return ids;
    }

    private static async Task ExecuteAsync(
        MySqlConnection connection, string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using var command = new MySqlCommand(sql, connection) { CommandTimeout = 300 };
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync(ct);
    }
}
