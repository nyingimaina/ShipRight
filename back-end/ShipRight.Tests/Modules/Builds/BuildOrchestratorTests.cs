using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Builds;

namespace ShipRight.Tests.Modules.Builds;

[TestClass]
public class BuildOrchestratorTests
{
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
}
