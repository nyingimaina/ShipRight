using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Server;

namespace ShipRight.Tests.Modules.Deployment;

[TestClass]
public class CloudConfigurationTests
{
    [TestMethod]
    public void ResolveAllowedOrigins_EnvironmentValueOverridesAppSettings()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cors:AllowedOrigins:0"] = "https://stale.example.com",
            })
            .Build();

        var origins = CloudConfiguration.ResolveAllowedOrigins(
            configuration,
            "https://shipright.example.com, https://admin.example.com");

        CollectionAssert.AreEqual(
            new[] { "https://shipright.example.com", "https://admin.example.com" },
            origins);
    }

    [TestMethod]
    public void ValidateProductionSettings_RejectsPlaceholderValues()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DatabaseConnection"] = "Server=db;Password=changeme",
                ["JwtKey"] = "CHANGE-ME-TO-A-RANDOM-64-CHAR-HEX-STRING",
                ["AdminEmail"] = "admin@example.com",
                ["AdminPassword"] = "changeme",
                ["CorsOrigins"] = "https://shipright.example.com",
            })
            .Build();

        Assert.ThrowsException<InvalidOperationException>(() =>
            CloudConfiguration.ValidateProductionSettings(configuration));
    }

    [TestMethod]
    public void ValidateProductionSettings_RejectsLocalhostCorsOrigin()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DatabaseConnection"] = "Server=db;Password=production-secret",
                ["JwtKey"] = "production-signing-key",
                ["AdminEmail"] = "ops@shipright.example.com",
                ["AdminPassword"] = "production-password",
                ["CorsOrigins"] = "http://localhost:5200",
            })
            .Build();

        Assert.ThrowsException<InvalidOperationException>(() =>
            CloudConfiguration.ValidateProductionSettings(configuration));
    }

    [TestMethod]
    public void CloudRegistration_UsesGlobalRateLimiter()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:SigningKey"] = "test-signing-key",
        });

        ShipRight.Server.CloudDiRegistrar.Register(services, configuration);

        using var provider = services.BuildServiceProvider();
        Assert.IsNotNull(provider.GetRequiredService<IOptions<RateLimiterOptions>>().Value.GlobalLimiter);
    }
}
