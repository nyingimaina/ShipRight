using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Projects;
using ShipRight.Modules.RemoteHost;
using ShipRight.Modules.WatchBranch;

namespace ShipRight.Tests.Database;

[TestClass]
public class CloudDiRegistrarTests
{
    private static ServiceProvider BuildCloudProvider()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationManager();
        config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:SigningKey"] = "test-signing-key-for-cloud-di-registrar-tests",
        });
        ShipRight.Server.CloudDiRegistrar.Register(services, config);
        return services.BuildServiceProvider();
    }

    [TestMethod]
    public void CloudDiProvider_ResolvesSshKeyStore()
    {
        using var provider = BuildCloudProvider();
        Assert.IsNotNull(provider.GetRequiredService<SshKeyStore>());
    }

    [TestMethod]
    public void CloudMode_SshKeyStatusRoute_BindsRegisteredStores_WithoutEndpointFailure()
    {
        using var provider = BuildCloudProvider();
        var options = new RequestDelegateFactoryOptions
        {
            ServiceProvider = provider,
            RouteParameterNames = ["id"],
            DisableInferBodyFromParameters = true,
        };

        var factory = RequestDelegateFactory.Create(
            (string id, IProjectStore store, SshKeyStore keyStore) =>
                Results.Ok(new { exists = false, publicKey = (string?)null }), options);

        Assert.IsNotNull(factory);
    }

    [TestMethod]
    public void CloudMode_DesktopWatchBranchHistoryStore_NotRegistered_ThrowsAtMetadataCreation()
    {
        using var provider = BuildCloudProvider();
        var options = new RequestDelegateFactoryOptions
        {
            ServiceProvider = provider,
            DisableInferBodyFromParameters = true,
        };

        Assert.ThrowsException<InvalidOperationException>(() =>
            RequestDelegateFactory.Create(
                (WatchBranchHistoryStore store, string? projectId = null, string? status = null, int limit = 100) =>
                    Results.Ok(new { records = new object[0] }), options));
    }

    [TestMethod]
    public void CloudMode_MariaDbWatchBranchHistoryRoute_BindsRegisteredStore_WithoutEndpointFailure()
    {
        using var provider = BuildCloudProvider();
        var options = new RequestDelegateFactoryOptions
        {
            ServiceProvider = provider,
            DisableInferBodyFromParameters = true,
        };

        var factory = RequestDelegateFactory.Create(
            (MariaDbWatchBranchHistoryStore store, string? projectId = null, string? status = null, int limit = 100) =>
                Results.Ok(new { records = new object[0] }), options);

        Assert.IsNotNull(factory);
    }
}
