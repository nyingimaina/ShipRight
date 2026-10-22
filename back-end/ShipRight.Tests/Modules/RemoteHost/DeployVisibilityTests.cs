using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Projects;
using ShipRight.Modules.RemoteHost;

namespace ShipRight.Tests.Modules.RemoteHost;

/// <summary>
/// A deploy must say which image tags it is pulling, and a failed one must show why each container is unhealthy
/// (the compose "unhealthy" message alone says nothing about the cause).
/// </summary>
[TestClass]
public class DeployVisibilityTests
{
    private static ServerConfig Server() => new()
    {
        Host = "h", Username = "u", SshKeyPath = "/k", RemoteWorkingDir = "/home/ubuntu/ship-right",
    };

    [TestMethod]
    public void VerboseDeployCommand_PrintsResolvedImagesBeforePulling()
    {
        var cmd = RemoteServerDeployer.BuildVerboseDeployCommand(Server());
        var images = cmd.IndexOf("docker compose config --images", StringComparison.Ordinal);
        var pull = cmd.IndexOf("docker compose pull", StringComparison.Ordinal);
        Assert.IsTrue(images >= 0, "should list the resolved images");
        Assert.IsTrue(images < pull, "images must be listed before the pull starts");
    }

    [TestMethod]
    public void VerboseDeployCommand_StillPullsAndRestarts_AndShowsRunningImages()
    {
        var cmd = RemoteServerDeployer.BuildVerboseDeployCommand(Server());
        StringAssert.Contains(cmd, "git pull");
        StringAssert.Contains(cmd, "docker compose up -d --remove-orphans");
        StringAssert.Contains(cmd, "{{.Image}}");
    }

    [TestMethod]
    public void VerboseDeployCommand_KeepsComposeUpsExitCode()
    {
        var cmd = RemoteServerDeployer.BuildVerboseDeployCommand(Server());
        StringAssert.Contains(cmd, "rc=$?");
        StringAssert.Contains(cmd, "exit $rc");
    }

    [TestMethod]
    public void ContainerDiagnostics_ShowsEachContainersHealthAndItsLogs()
    {
        var cmd = RemoteCheckpoint.BuildContainerDiagnosticsCommand(Server());
        StringAssert.Contains(cmd, "cd '/home/ubuntu/ship-right'");
        StringAssert.Contains(cmd, "{{.Image}}");
        StringAssert.Contains(cmd, "docker inspect");
        StringAssert.Contains(cmd, ".State.Health");
        StringAssert.Contains(cmd, "docker logs --tail 40");
    }

    [TestMethod]
    public void ContainerDiagnostics_NeverFails_SoItCannotMaskTheRealError()
    {
        StringAssert.EndsWith(RemoteCheckpoint.BuildContainerDiagnosticsCommand(Server()).TrimEnd(), "true");
    }
}
