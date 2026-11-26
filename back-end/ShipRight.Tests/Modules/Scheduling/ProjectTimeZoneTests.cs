using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Projects;
using ShipRight.Shared.Time;

namespace ShipRight.Tests.Modules.Scheduling;

[TestClass]
public class ProjectTimeZoneTests
{
    [TestMethod]
    public void ProjectConfig_DefaultsTimeZoneToTheAppZone()
    {
        Assert.AreEqual(AppTimeZone.DefaultId, new ProjectConfig().TimeZone);
        Assert.AreEqual("Africa/Nairobi", AppTimeZone.DefaultId);
    }
}
