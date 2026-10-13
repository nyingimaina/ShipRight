using Microsoft.Extensions.Configuration;

namespace ShipRight.Server;

// Centralizes cloud-only startup validation so unsafe defaults fail before the server listens.
public static class CloudConfiguration
{
    // Every setting the cloud app reads. docker-compose.yml and .env.example must list exactly these
    // (checked by a test); anything else left in someone's environment is ignored.
    public static readonly string[] SettingNames =
    [
        "SHIPRIGHT__MODE",
        "SHIPRIGHT__TIME_ZONE",
        "SHIPRIGHT__DB_CONNECTION",
        "SHIPRIGHT__CORS_ORIGINS",
        "SHIPRIGHT__PUBLIC_URL",
        "SHIPRIGHT__SMTP_HOST",
        "SHIPRIGHT__SMTP_PORT",
        "SHIPRIGHT__SMTP_USERNAME",
        "SHIPRIGHT__SMTP_PASSWORD",
        "SHIPRIGHT__SMTP_FROM",
        "SHIPRIGHT__GOOGLE_CLIENT_ID",
        "SHIPRIGHT__GOOGLE_CLIENT_SECRET",
    ];

    public static string[] ResolveAllowedOrigins(IConfiguration configuration, string? environmentValue)
    {
        var origins = environmentValue is not null
            ? environmentValue.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            : configuration.GetSection("Cors:AllowedOrigins").Get<string[]>();

        return origins is { Length: > 0 }
            ? origins
            : throw new InvalidOperationException("At least one CORS origin is required in cloud mode.");
    }

    public static void ValidateCloudSettings(IConfiguration configuration)
    {
        ValidateCloudSettings(
            configuration["DatabaseConnection"] ?? configuration["SHIPRIGHT__DB_CONNECTION"],
            configuration["CorsOrigins"] ?? configuration["SHIPRIGHT__CORS_ORIGINS"],
            configuration["GoogleClientId"] ?? configuration["SHIPRIGHT__GOOGLE_CLIENT_ID"],
            configuration["GoogleClientSecret"] ?? configuration["SHIPRIGHT__GOOGLE_CLIENT_SECRET"]);
    }

    // Messages name the setting and never the value, so a secret cannot leak through a start-up error.
    public static void ValidateCloudSettings(
        string? connection, string? corsOrigins,
        string? googleClientId = null, string? googleClientSecret = null)
    {
        ValidateRequired(connection, "SHIPRIGHT__DB_CONNECTION");
        ValidateRequired(corsOrigins, "SHIPRIGHT__CORS_ORIGINS");

        if (connection!.Contains("changeme", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("SHIPRIGHT__DB_CONNECTION contains a placeholder password.");
        if (corsOrigins!.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Any(origin => origin.StartsWith("http://localhost", StringComparison.OrdinalIgnoreCase)
                        || origin.StartsWith("http://127.0.0.1", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("SHIPRIGHT__CORS_ORIGINS must not contain localhost in production.");

        // Google is optional, but half of it is a mistake worth reporting rather than silently hiding the button.
        var hasId = !string.IsNullOrWhiteSpace(googleClientId);
        var hasSecret = !string.IsNullOrWhiteSpace(googleClientSecret);
        if (hasId && !hasSecret)
            throw new InvalidOperationException("SHIPRIGHT__GOOGLE_CLIENT_SECRET is required when SHIPRIGHT__GOOGLE_CLIENT_ID is set.");
        if (hasSecret && !hasId)
            throw new InvalidOperationException("SHIPRIGHT__GOOGLE_CLIENT_ID is required when SHIPRIGHT__GOOGLE_CLIENT_SECRET is set.");
    }

    private static void ValidateRequired(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"{name} is required in cloud mode.");
    }
}
