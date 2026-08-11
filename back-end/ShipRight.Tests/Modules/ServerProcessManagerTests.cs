using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Desktop.Services;

namespace ShipRight.Tests.Modules;

[TestClass]
public class ServerProcessManagerTests
{
    [TestMethod]
    public void EvaluateExistingServer_CompatibleVersionsAndWindowsDataDir_ReturnsCompatible()
    {
        var result = ServerProcessManager.EvaluateExistingServer(
            "3.6.6", "3.6.6", @"C:\Users\nying\.shipright");

        Assert.AreEqual(ServerProcessManager.ServerTrust.Compatible, result);
    }

    [TestMethod]
    public void EvaluateExistingServer_PatchDriftWithWindowsDataDir_ReturnsCompatible()
    {
        var result = ServerProcessManager.EvaluateExistingServer(
            "3.6.6", "3.6.5", @"C:\Users\nying\.shipright");

        Assert.AreEqual(ServerProcessManager.ServerTrust.Compatible, result);
    }

    [TestMethod]
    public void EvaluateExistingServer_MinorDriftWithWindowsDataDir_ReturnsVersionDrift()
    {
        var result = ServerProcessManager.EvaluateExistingServer(
            "3.6.6", "3.5.0", @"C:\Users\nying\.shipright");

        Assert.AreEqual(ServerProcessManager.ServerTrust.VersionDrift, result);
    }

    [TestMethod]
    public void EvaluateExistingServer_MajorDriftWithWindowsDataDir_ReturnsVersionDrift()
    {
        var result = ServerProcessManager.EvaluateExistingServer(
            "3.6.6", "1.0.0", @"C:\Users\nying\.shipright");

        Assert.AreEqual(ServerProcessManager.ServerTrust.VersionDrift, result);
    }

    [TestMethod]
    public void EvaluateExistingServer_LinuxDataDir_ReturnsExternalHost()
    {
        var result = ServerProcessManager.EvaluateExistingServer(
            "3.6.6", "1.0.0", "/root/.shipright");

        Assert.AreEqual(ServerProcessManager.ServerTrust.ExternalHost, result);
    }

    [TestMethod]
    public void EvaluateExistingServer_WslDataDir_ReturnsExternalHost()
    {
        var result = ServerProcessManager.EvaluateExistingServer(
            "3.6.6", "1.0.0", @"\\wsl.localhost\Ubuntu\root\.shipright");

        Assert.AreEqual(ServerProcessManager.ServerTrust.ExternalHost, result);
    }

    [TestMethod]
    public void EvaluateExistingServer_NullServerVersion_ReturnsExternalHost()
    {
        var result = ServerProcessManager.EvaluateExistingServer(
            "3.6.6", null, @"C:\Users\nying\.shipright");

        Assert.AreEqual(ServerProcessManager.ServerTrust.ExternalHost, result);
    }

    [TestMethod]
    public void EvaluateExistingServer_NullDataDirectory_ReturnsExternalHost()
    {
        var result = ServerProcessManager.EvaluateExistingServer(
            "3.6.6", "3.6.6", null);

        Assert.AreEqual(ServerProcessManager.ServerTrust.ExternalHost, result);
    }

    [TestMethod]
    public void EvaluateExistingServer_UnparseableServerVersion_ReturnsExternalHost()
    {
        var result = ServerProcessManager.EvaluateExistingServer(
            "3.6.6", "not-a-version", @"C:\Users\nying\.shipright");

        Assert.AreEqual(ServerProcessManager.ServerTrust.ExternalHost, result);
    }

    [TestMethod]
    public void CheckVersionDrift_SameVersion_ReturnsNone()
    {
        Assert.AreEqual(ServerProcessManager.VersionDrift.None,
            ServerProcessManager.CheckVersionDrift("3.6.6", "3.6.6"));
    }

    [TestMethod]
    public void CheckVersionDrift_PatchMismatch_ReturnsNone()
    {
        Assert.AreEqual(ServerProcessManager.VersionDrift.None,
            ServerProcessManager.CheckVersionDrift("3.6.6", "3.6.5"));
    }

    [TestMethod]
    public void CheckVersionDrift_MinorMismatch_ReturnsMinor()
    {
        Assert.AreEqual(ServerProcessManager.VersionDrift.Minor,
            ServerProcessManager.CheckVersionDrift("3.6.6", "3.5.0"));
    }

    [TestMethod]
    public void CheckVersionDrift_MajorMismatch_ReturnsMajor()
    {
        Assert.AreEqual(ServerProcessManager.VersionDrift.Major,
            ServerProcessManager.CheckVersionDrift("3.6.6", "1.0.0"));
    }

    [TestMethod]
    public void CheckVersionDrift_Unparseable_ReturnsUnknown()
    {
        Assert.AreEqual(ServerProcessManager.VersionDrift.Unknown,
            ServerProcessManager.CheckVersionDrift("3.6.6", "abc"));
    }

    [TestMethod]
    public void BuildConflictMessage_ExternalHost_IncludesPortVersionAndDataDirectory()
    {
        var message = ServerProcessManager.BuildConflictMessage(
            ServerProcessManager.ServerTrust.ExternalHost,
            "3.6.6", "1.0.0", "3.6.4", "/root/.shipright");

        StringAssert.Contains(message, "5200");
        StringAssert.Contains(message, "1.0.0");
        StringAssert.Contains(message, "/root/.shipright");
    }

    [TestMethod]
    public void BuildConflictMessage_VersionDrift_IncludesBothVersions()
    {
        var message = ServerProcessManager.BuildConflictMessage(
            ServerProcessManager.ServerTrust.VersionDrift,
            "3.6.6", "1.0.0", "3.6.4", @"C:\Users\nying\.shipright");

        StringAssert.Contains(message, "3.6.6");
        StringAssert.Contains(message, "1.0.0");
    }
}
