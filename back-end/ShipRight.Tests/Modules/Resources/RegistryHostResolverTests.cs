using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Projects;
using ShipRight.Modules.Resources;
using ShipRight.Modules.Resources.Models;

namespace ShipRight.Tests.Modules.Resources;

[TestClass]
public class RegistryHostResolverTests
{
    [TestMethod]
    public void Resolve_ExplicitSvcRegistry_BoundResourceIgnored()
    {
        var svc = new ServiceConfig { DockerRegistry = "ghcr.io", DockerImageName = "org/app" };
        var resource = new DockerRegistryResource { Registry = "123.dkr.ecr.us-east-1.amazonaws.com" };

        Assert.AreEqual("ghcr.io", RegistryHostResolver.Resolve(svc, resource));
    }

    [TestMethod]
    public void Resolve_NoSvcRegistry_BoundResourceRegistryWins()
    {
        var svc = new ServiceConfig { DockerImageName = "org/app" };
        var resource = new DockerRegistryResource { Registry = "123.dkr.ecr.us-east-1.amazonaws.com" };

        Assert.AreEqual("123.dkr.ecr.us-east-1.amazonaws.com", RegistryHostResolver.Resolve(svc, resource));
    }

    [TestMethod]
    public void Resolve_NoSvcRegistry_EmptyResourceRegistry_FallsBackToImagePrefix()
    {
        var svc = new ServiceConfig { DockerImageName = "ghcr.io/org/app" };
        var resource = new DockerRegistryResource { Registry = "" };

        Assert.AreEqual("ghcr.io", RegistryHostResolver.Resolve(svc, resource));
    }

    [TestMethod]
    public void Resolve_NoSvcRegistry_NoResource_ImagePrefix()
    {
        var svc = new ServiceConfig { DockerImageName = "ghcr.io/org/app" };

        Assert.AreEqual("ghcr.io", RegistryHostResolver.Resolve(svc, null));
    }

    [TestMethod]
    public void Resolve_NoSvcRegistry_ResourceOnlyRegistry_EmptyImage()
    {
        var svc = new ServiceConfig { DockerImageName = "" };
        var resource = new DockerRegistryResource { Registry = "123.dkr.ecr.us-east-1.amazonaws.com" };

        Assert.AreEqual("123.dkr.ecr.us-east-1.amazonaws.com", RegistryHostResolver.Resolve(svc, resource));
    }

    [TestMethod]
    public void DockerRegistryResource_Defaults_AreBackwardCompatible()
    {
        var resource = new DockerRegistryResource();

        Assert.AreEqual(RegistryAuthType.Password, resource.AuthType);
        Assert.AreEqual("", resource.AwsRegion);
    }
}
