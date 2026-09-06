using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Projects;

namespace ShipRight.Tests.Modules.Scheduling;

[TestClass]
public class ProjectTimeZoneTests
{
    [TestMethod]
    public void ProjectConfig_DefaultsTimeZoneToUtc()
    {
        Assert.AreEqual("UTC", new ProjectConfig().TimeZone);
    }
}
