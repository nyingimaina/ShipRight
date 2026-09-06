using Microsoft.Extensions.Configuration;

namespace ShipRight.Server;

// Centralizes cloud-only startup validation so unsafe defaults fail before the server listens.
public static class CloudConfiguration
{
    private const string JwtPlaceholder = "CHANGE-ME-TO-A-RANDOM-64-CHAR-HEX-STRING";

    public static string[] ResolveAllowedOrigins(IConfiguration configuration, string? environmentValue)
    {
        var origins = environmentValue is not null
            ? environmentValue.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            : configuration.GetSection("Cors:AllowedOrigins").Get<string[]>();

        return origins is { Length: > 0 }
            ? origins
            : throw new InvalidOperationException("At least one CORS origin is required in cloud mode.");
    }

    public static void ValidateProductionSettings(IConfiguration configuration)
    {
        var connection = configuration["DatabaseConnection"] ?? configuration["SHIPRIGHT__DB_CONNECTION"];
        var jwtKey = configuration["JwtKey"] ?? configuration["SHIPRIGHT__JWT_KEY"];
        var adminEmail = configuration["AdminEmail"] ?? configuration["SHIPRIGHT__ADMIN_EMAIL"];
        var adminPassword = configuration["AdminPassword"] ?? configuration["SHIPRIGHT__ADMIN_PASSWORD"];
        var corsOrigins = configuration["CorsOrigins"] ?? configuration["SHIPRIGHT__CORS_ORIGINS"];

        ValidateRequired(connection, "SHIPRIGHT__DB_CONNECTION");
        ValidateRequired(jwtKey, "SHIPRIGHT__JWT_KEY");
        ValidateRequired(adminEmail, "SHIPRIGHT__ADMIN_EMAIL");
        ValidateRequired(adminPassword, "SHIPRIGHT__ADMIN_PASSWORD");
        ValidateRequired(corsOrigins, "SHIPRIGHT__CORS_ORIGINS");

        if (connection!.Contains("changeme", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("SHIPRIGHT__DB_CONNECTION contains a placeholder password.");
        if (string.Equals(jwtKey, JwtPlaceholder, StringComparison.Ordinal))
            throw new InvalidOperationException("SHIPRIGHT__JWT_KEY contains the sample signing key.");
        if (string.Equals(adminEmail, "admin@example.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("SHIPRIGHT__ADMIN_EMAIL must be changed for production.");
        if (string.Equals(adminPassword, "changeme", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("SHIPRIGHT__ADMIN_PASSWORD must be changed for production.");
        if (corsOrigins!.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Any(origin => origin.StartsWith("http://localhost", StringComparison.OrdinalIgnoreCase)
                        || origin.StartsWith("http://127.0.0.1", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("SHIPRIGHT__CORS_ORIGINS must not contain localhost in production.");
    }

    public static void ValidateProductionSettings(
        string? connection, string? jwtKey, string? adminEmail, string? adminPassword,
        string? corsOrigins = "https://configured.example.com")
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DatabaseConnection"] = connection,
                ["JwtKey"] = jwtKey,
                ["AdminEmail"] = adminEmail,
                ["AdminPassword"] = adminPassword,
                ["CorsOrigins"] = corsOrigins,
            })
            .Build();
        ValidateProductionSettings(configuration);
    }

    private static void ValidateRequired(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"{name} is required in cloud mode.");
    }
}
