using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Resources;
using ShipRight.Modules.Resources.Models;

namespace ShipRight.Tests.Modules.Resources;

[TestClass]
public class RegistryAuthProviderTests
{
    [TestMethod]
    public void GetFor_DockerHubHost_ReturnsDockerHubProvider()
    {
        var registry = RegistryAuthProviderRegistry.CreateDefault();

        var provider = registry.GetFor("docker.io", null);

        Assert.IsInstanceOfType<DockerHubAuthProvider>(provider);
        Assert.IsTrue(provider.IsDockerHub);
    }

    [TestMethod]
    public void GetFor_IndexDockerIo_ReturnsDockerHubProvider()
    {
        var registry = RegistryAuthProviderRegistry.CreateDefault();

        var provider = registry.GetFor("index.docker.io", null);

        Assert.IsTrue(provider.IsDockerHub);
    }

    [TestMethod]
    public void GetFor_GhcrHost_ReturnsStandardProvider()
    {
        var registry = RegistryAuthProviderRegistry.CreateDefault();

        var provider = registry.GetFor("ghcr.io", null);

        Assert.IsInstanceOfType<StandardAuthProvider>(provider);
        Assert.IsFalse(provider.IsDockerHub);
    }

    [TestMethod]
    public void GetFor_EcrHostNoResource_ReturnsAwsEcrProvider()
    {
        var registry = RegistryAuthProviderRegistry.CreateDefault();

        var provider = registry.GetFor("123.dkr.ecr.us-east-1.amazonaws.com", null);

        Assert.IsInstanceOfType<AwsEcrAuthProvider>(provider);
        Assert.IsFalse(provider.IsDockerHub);
    }

    [TestMethod]
    public void GetFor_EcrResourceWithAwsEcrAuthType_ReturnsAwsEcrProvider()
    {
        var registry = RegistryAuthProviderRegistry.CreateDefault();
        var resource = new DockerRegistryResource { AuthType = RegistryAuthType.AwsEcr };

        var provider = registry.GetFor("123.dkr.ecr.us-east-1.amazonaws.com", resource);

        Assert.IsInstanceOfType<AwsEcrAuthProvider>(provider);
    }

    [TestMethod]
    public void Registry_WithNoProviders_Throws()
    {
        Assert.ThrowsException<ArgumentException>(() => new RegistryAuthProviderRegistry([]));
    }

    [TestMethod]
    public async Task GetLoginCredentials_ResourceBound_PrefersResourceOverFallback()
    {
        var provider = new StandardAuthProvider();
        var resource = new DockerRegistryResource
        {
            Username = "resource-user",
            Password = "resource-pass",
        };

        var (username, password) = await provider.GetLoginCredentialsAsync("ghcr.io", resource, "fallback-user", "fallback-pass");

        Assert.AreEqual("resource-user", username);
        Assert.AreEqual("resource-pass", password);
    }

    [TestMethod]
    public async Task GetLoginCredentials_NoResource_UsesFallback()
    {
        var provider = new DockerHubAuthProvider();

        var (username, password) = await provider.GetLoginCredentialsAsync("docker.io", null, "fallback-user", "fallback-pass");

        Assert.AreEqual("fallback-user", username);
        Assert.AreEqual("fallback-pass", password);
    }
}
