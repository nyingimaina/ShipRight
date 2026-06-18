using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Desktop.Services;

namespace ShipRight.Tests;

[TestClass]
public class DesktopNavigationTests
{
    [TestMethod]
    public void ComputeBackoffDelay_FirstAttempt_ReturnsBaseDelay()
    {
        var result = ServerProcessManager.ComputeBackoffDelay(
            attemptIndex: 0, baseDelay: TimeSpan.FromMilliseconds(200), maxDelay: TimeSpan.FromSeconds(5));
        Assert.AreEqual(200, result.TotalMilliseconds, 1);
    }

    [TestMethod]
    public void ComputeBackoffDelay_SecondAttempt_Doubles()
    {
        var result = ServerProcessManager.ComputeBackoffDelay(
            attemptIndex: 1, baseDelay: TimeSpan.FromMilliseconds(200), maxDelay: TimeSpan.FromSeconds(5));
        Assert.AreEqual(400, result.TotalMilliseconds, 1);
    }

    [TestMethod]
    public void ComputeBackoffDelay_ClampsToMax()
    {
        var result = ServerProcessManager.ComputeBackoffDelay(
            attemptIndex: 10, baseDelay: TimeSpan.FromMilliseconds(200), maxDelay: TimeSpan.FromMilliseconds(1000));
        Assert.AreEqual(1000, result.TotalMilliseconds, 1);
    }

    [TestMethod]
    public void IsPortInUse_ReturnsFalseForUnlikelyPort()
    {
        Assert.IsFalse(ServerProcessManager.IsPortInUse(65535));
    }

    [TestMethod]
    public void CheckVersionDrift_NullVersions_ReturnsUnknown()
    {
        Assert.AreEqual(ServerProcessManager.VersionDrift.Unknown,
            ServerProcessManager.CheckVersionDrift(null, "1.0.0"));
        Assert.AreEqual(ServerProcessManager.VersionDrift.Unknown,
            ServerProcessManager.CheckVersionDrift("1.0.0", null));
    }

    [TestMethod]
    public void CheckVersionDrift_SameVersion_ReturnsNone()
    {
        Assert.AreEqual(ServerProcessManager.VersionDrift.None,
            ServerProcessManager.CheckVersionDrift("1.2.3", "1.2.3"));
    }

    [TestMethod]
    public void CheckVersionDrift_MinorDifference_ReturnsMinor()
    {
        Assert.AreEqual(ServerProcessManager.VersionDrift.Minor,
            ServerProcessManager.CheckVersionDrift("1.3.0", "1.2.0"));
    }

    [TestMethod]
    public void CheckVersionDrift_MajorDifference_ReturnsMajor()
    {
        Assert.AreEqual(ServerProcessManager.VersionDrift.Major,
            ServerProcessManager.CheckVersionDrift("2.0.0", "1.0.0"));
    }

    [TestMethod]
    public void CheckVersionDrift_InvalidVersion_ReturnsUnknown()
    {
        Assert.AreEqual(ServerProcessManager.VersionDrift.Unknown,
            ServerProcessManager.CheckVersionDrift("not-a-version", "1.0.0"));
    }

    [TestMethod]
    public async Task PollWithExponentialBackoffAsync_RetriesAndFails()
    {
        var attempts = 0;
        var result = await ServerProcessManager.PollWithExponentialBackoffAsync(() =>
        {
            attempts++;
            return Task.FromResult(false);
        }, maxRetries: 3);

        Assert.IsFalse(result);
        Assert.AreEqual(3, attempts);
    }

    [TestMethod]
    public async Task PollWithExponentialBackoffAsync_SucceedsOnThirdAttempt()
    {
        var attempts = 0;
        var result = await ServerProcessManager.PollWithExponentialBackoffAsync(() =>
        {
            attempts++;
            return Task.FromResult(attempts >= 3);
        }, maxRetries: 5);

        Assert.IsTrue(result);
        Assert.AreEqual(3, attempts);
    }
}
