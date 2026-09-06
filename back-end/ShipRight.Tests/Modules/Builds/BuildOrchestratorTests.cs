using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Builds;
using ShipRight.Modules.Projects;

namespace ShipRight.Tests.Modules.Builds;

[TestClass]
public class BuildOrchestratorTests
{
    [TestMethod]
    public void ResolveRegistry_ExplicitDockerRegistryField_ReturnsThatHost()
    {
        var svc = new ServiceConfig { DockerRegistry = "ghcr.io", DockerImageName = "org/app" };
        Assert.AreEqual("ghcr.io", BuildOrchestrator.ResolveRegistry(svc));
    }

    [TestMethod]
    public void ResolveRegistry_NoDockerRegistry_ImageWithHostPrefix_ReturnsPrefix()
    {
        var svc = new ServiceConfig { DockerImageName = "ghcr.io/org/app" };
        Assert.AreEqual("ghcr.io", BuildOrchestrator.ResolveRegistry(svc));
    }

    [TestMethod]
    public void ResolveRegistry_NoDockerRegistry_ImageWithPortPrefix_ReturnsPrefix()
    {
        var svc = new ServiceConfig { DockerImageName = "localhost:5000/org/app" };
        Assert.AreEqual("localhost:5000", BuildOrchestrator.ResolveRegistry(svc));
    }

    [TestMethod]
    public void ResolveRegistry_NoDockerRegistry_HubStyleImage_ReturnsDockerIo()
    {
        var svc = new ServiceConfig { DockerImageName = "nyingi/app" };
        Assert.AreEqual("docker.io", BuildOrchestrator.ResolveRegistry(svc));
    }

    [TestMethod]
    public void ResolveRegistry_NoDockerRegistry_SingleSegmentImage_ReturnsDockerIo()
    {
        var svc = new ServiceConfig { DockerImageName = "nginx" };
        Assert.AreEqual("docker.io", BuildOrchestrator.ResolveRegistry(svc));
    }

    [TestMethod]
    public void ResolveRegistry_NoDockerRegistry_EmptyImage_ReturnsDockerIo()
    {
        var svc = new ServiceConfig { DockerImageName = "" };
        Assert.AreEqual("docker.io", BuildOrchestrator.ResolveRegistry(svc));
    }

    [TestMethod]
    public void ExtractImageOwner_GivenDockerHubUserImage_ReturnsOwner()
    {
        var result = BuildOrchestrator.ExtractImageOwner("nyingi/lattice-foundation", "docker.io");
        Assert.AreEqual("nyingi", result);
    }

    [TestMethod]
    public void ExtractImageOwner_GivenOfficialImageLibrary_ReturnsNull()
    {
        var result = BuildOrchestrator.ExtractImageOwner("library/nginx", "docker.io");
        Assert.IsNull(result);
    }

    [TestMethod]
    public void ExtractImageOwner_GivenSingleSegmentImage_ReturnsNull()
    {
        var result = BuildOrchestrator.ExtractImageOwner("nginx", "docker.io");
        Assert.IsNull(result);
    }

    [TestMethod]
    public void ExtractImageOwner_GivenCustomRegistry_ReturnsNull()
    {
        var result = BuildOrchestrator.ExtractImageOwner("myorg/myimage", "myregistry.example.com");
        Assert.IsNull(result);
    }

    [TestMethod]
    public void ExtractImageOwner_GivenIndexDockerIo_ReturnsOwner()
    {
        var result = BuildOrchestrator.ExtractImageOwner("nyingi/app", "index.docker.io");
        Assert.AreEqual("nyingi", result);
    }

    [TestMethod]
    public void ExtractImageOwner_GivenEmptyImageName_ReturnsNull()
    {
        var result = BuildOrchestrator.ExtractImageOwner("", "docker.io");
        Assert.IsNull(result);
    }

    [TestMethod]
    public void ParseDockerInfoForUsername_GivenStandardOutput_ReturnsUsername()
    {
        var output = @"Client:
 Context:    default
 Debug Mode: false

Server:
 Containers: 0
  Running: 0
  Paused: 0
  Stopped: 0
 Images: 5
 Server Version: 24.0.7
 Username: nyingi
 Operating System: Linux
";
        var result = BuildOrchestrator.ParseDockerInfoForUsername(output);
        Assert.AreEqual("nyingi", result);
    }

    [TestMethod]
    public void ParseDockerInfoForUsername_GivenEmptyOutput_ReturnsNull()
    {
        Assert.IsNull(BuildOrchestrator.ParseDockerInfoForUsername(""));
    }

    [TestMethod]
    public void ParseDockerInfoForUsername_WhenUsernameMissing_ReturnsNull()
    {
        var output = @"Client:
 Context:    default
 Debug Mode: false

Server:
 Containers: 0
";
        Assert.IsNull(BuildOrchestrator.ParseDockerInfoForUsername(output));
    }

[TestMethod]
    public void ParseDockerInfoForUsername_WhenNotLoggedIn_ReturnsNull()
    {
        var output = @"Client:
 Context:    default
 Debug Mode: false

Server:
 Containers: 0
 ";
        Assert.IsNull(BuildOrchestrator.ParseDockerInfoForUsername(output));
    }

    [TestMethod]
    public void AppendBuildKitCacheArgs_ImageExistsLocally_AddsCacheFromAndInlineCacheArgs()
    {
        var args = new List<string> { "build", "--progress=plain" };

        BuildOrchestrator.AppendBuildKitCacheArgs(args, "shipright/app:4.3.1", imageExistsLocally: true);

        CollectionAssert.Contains(args, "--cache-from");
        CollectionAssert.Contains(args, "shipright/app:4.3.1");
        CollectionAssert.Contains(args, "--build-arg");
        CollectionAssert.Contains(args, "BUILDKIT_INLINE_CACHE=1");
    }

    [TestMethod]
    public void AppendBuildKitCacheArgs_ImageNotLocal_AddsNoCacheArgs()
    {
        var args = new List<string> { "build", "--progress=plain" };

        BuildOrchestrator.AppendBuildKitCacheArgs(args, "shipright/app:4.3.1", imageExistsLocally: false);

        CollectionAssert.DoesNotContain(args, "--cache-from");
        CollectionAssert.DoesNotContain(args, "BUILDKIT_INLINE_CACHE=1");
        CollectionAssert.AreEqual(new[] { "build", "--progress=plain" }, args.ToArray());
    }

    [TestMethod]
    public void AppendBuildKitCacheArgs_EmptyBuildArgs_StartsWithCacheFromFlag()
    {
        var args = new List<string>();

        BuildOrchestrator.AppendBuildKitCacheArgs(args, "shipright/app:4.3.1", imageExistsLocally: true);

        Assert.AreEqual("--cache-from", args[0]);
    }
}
