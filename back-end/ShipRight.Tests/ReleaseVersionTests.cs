using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Projects;

namespace ShipRight.Tests;

[TestClass]
public class ReleaseVersionTests
{
    [TestMethod]
    public void ServerAssembly_UsesCurrentReleaseVersion()
    {
        var version = typeof(ProjectConfig).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        Assert.AreEqual("4.3.1", version?.Split('+')[0]);
    }
}
