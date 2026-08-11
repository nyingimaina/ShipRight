using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Rocket.Libraries.DatabaseIntegrator;
using ShipRight.Database;

namespace ShipRight.Tests.Database;

[TestClass]
public class CloudDatabaseRegistrarTests
{
    private const string EnvConnectionString =
        "Server=db;Port=3306;Database=shipright;User Id=shipright;Password=secret;Charset=utf8;ConvertZeroDateTime=True;";

    private const string DefaultConnectionString =
        "Server=localhost;Port=3306;Database=shipright;User Id=root;Password=;Charset=utf8;ConvertZeroDateTime=True;";

    [TestMethod]
    public void Register_WithEnvironmentConnectionString_ConfiguresLibrarySettings()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationManager();

        CloudDatabaseRegistrar.Register(services, config, EnvConnectionString);

        var provider = services.BuildServiceProvider();
        var settings = provider.GetRequiredService<IOptions<DatabaseConnectionSettings>>();

        Assert.AreEqual(EnvConnectionString, settings.Value.ConnectionString);
    }

    [TestMethod]
    public void Register_WithAppSettingsConnectionString_ConfiguresLibrarySettings()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationManager();
        config["ConnectionStrings:DefaultConnection"] = EnvConnectionString;

        CloudDatabaseRegistrar.Register(services, config, null);

        var provider = services.BuildServiceProvider();
        var settings = provider.GetRequiredService<IOptions<DatabaseConnectionSettings>>();

        Assert.AreEqual(EnvConnectionString, settings.Value.ConnectionString);
    }

    [TestMethod]
    public void Register_WithoutAnyConfiguration_UsesDefaultLocalhostConnectionString()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationManager();

        CloudDatabaseRegistrar.Register(services, config, null);

        var provider = services.BuildServiceProvider();
        var settings = provider.GetRequiredService<IOptions<DatabaseConnectionSettings>>();

        Assert.AreEqual(DefaultConnectionString, settings.Value.ConnectionString);
    }

    [TestMethod]
    public void Register_RegistersDatabaseHelper_AndConnectionProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var config = new ConfigurationManager();

        CloudDatabaseRegistrar.Register(services, config, EnvConnectionString);

        var provider = services.BuildServiceProvider();

        Assert.IsNotNull(provider.GetRequiredService<IConnectionProvider>());
        Assert.IsNotNull(provider.GetRequiredService<IDatabaseHelper<Guid>>());
    }
}
