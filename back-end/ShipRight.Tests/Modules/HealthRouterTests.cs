using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.System;

namespace ShipRight.Tests.Modules;

[TestClass]
public class HealthRouterTests
{
    private static readonly DateTime StartedAt = new(2026, 8, 11, 12, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public void BuildHealthPayload_DesktopMode_ModeIsDesktop()
    {
        var payload = HealthRouter.BuildHealthPayload(
            AppMode.Desktop, "3.6.6", "3.6.6", @"C:\Users\nying\.shipright", 8, 12, StartedAt);

        Assert.AreEqual(AppMode.Desktop, payload.Mode);
    }

    [TestMethod]
    public void BuildHealthPayload_CloudMode_ModeIsCloud()
    {
        var payload = HealthRouter.BuildHealthPayload(
            AppMode.Cloud, "3.6.6", "3.6.6", "/root/.shipright", 8, 12, StartedAt);

        Assert.AreEqual(AppMode.Cloud, payload.Mode);
    }

    [TestMethod]
    public void BuildHealthPayload_ReturnsHealthyStatusAndIdentityFields()
    {
        var payload = HealthRouter.BuildHealthPayload(
            AppMode.Desktop, "3.6.6", "3.6.5", @"C:\Users\nying\.shipright", 8, 12, StartedAt);

        Assert.AreEqual("healthy", payload.Status);
        Assert.AreEqual("3.6.6", payload.ServerVersion);
        Assert.AreEqual("3.6.5", payload.WebVersion);
        Assert.AreEqual(@"C:\Users\nying\.shipright", payload.DataDirectory);
        Assert.AreEqual(8, payload.ProjectCount);
        Assert.AreEqual(12, payload.BuildCount);
        Assert.AreEqual(5200, payload.Port);
        Assert.AreEqual(StartedAt, payload.StartedAt);
    }
}
