using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.RuntimeConfig;

namespace ShipRight.Tests.RuntimeConfig;

[TestClass]
public class AppProfileTests
{
    private const string FakeProfile = @"C:\Users\alice";
    private const string FakeLocalAppData = @"C:\Users\alice\AppData\Local";

    [TestMethod]
    public void Resolve_NoProfile_MatchesLegacyDefaults()
    {
        var profile = AppProfile.Resolve(Array.Empty<string>(), null, null, null, FakeProfile, FakeLocalAppData);

        Assert.AreEqual("", profile.ProfileName);
        Assert.AreEqual(AppProfile.DefaultPort, profile.Port);
        Assert.AreEqual(@$"{FakeProfile}\.shipright", profile.DataDirectory);
        Assert.AreEqual(@"C:\Users\alice\AppData\Local\ShipRight", profile.AppHomeDirectory);
        Assert.AreEqual("ShipRight.Desktop", profile.MutexName);
        Assert.AreEqual(@"C:\Users\alice\AppData\Local\ShipRight\WebView2", profile.WebView2Directory);
    }

    [TestMethod]
    public void Resolve_ProfileFromArg_IsolatesEverySharedResource()
    {
        var args = new[] { "--profile=4" };
        var profile = AppProfile.Resolve(args, null, null, null, FakeProfile, FakeLocalAppData);

        Assert.AreEqual("4", profile.ProfileName);
        Assert.AreEqual(AppProfile.ProfiledDefaultPort, profile.Port);
        Assert.AreEqual(@$"{FakeProfile}\.shipright-4", profile.DataDirectory);
        Assert.AreEqual(@"C:\Users\alice\AppData\Local\ShipRight-4", profile.AppHomeDirectory);
        Assert.AreEqual("ShipRight.Desktop-4", profile.MutexName);
        Assert.AreEqual(@"C:\Users\alice\AppData\Local\ShipRight-4\WebView2", profile.WebView2Directory);
    }

    [TestMethod]
    public void Resolve_ProfileFromEnvironment_IsUsedWhenArgAbsent()
    {
        var profile = AppProfile.Resolve(Array.Empty<string>(), "4", null, null, FakeProfile, FakeLocalAppData);

        Assert.AreEqual("4", profile.ProfileName);
        Assert.AreEqual(AppProfile.ProfiledDefaultPort, profile.Port);
        Assert.AreEqual(@$"{FakeProfile}\.shipright-4", profile.DataDirectory);
    }

    [TestMethod]
    public void Resolve_ExplicitPortArg_OverridesProfiledDefault()
    {
        var args = new[] { "--profile=2", "--port=5305" };
        var profile = AppProfile.Resolve(args, null, null, null, FakeProfile, FakeLocalAppData);

        Assert.AreEqual(5305, profile.Port);
    }

    [TestMethod]
    public void Resolve_PortEnv_OverridesDefault()
    {
        var args = new[] { "--profile=4" };
        var profile = AppProfile.Resolve(args, null, "5306", null, FakeProfile, FakeLocalAppData);

        Assert.AreEqual(5306, profile.Port);
    }

    [TestMethod]
    public void Resolve_InvalidPort_FallsBackToDefault()
    {
        var args = new[] { "--port=abc" };
        var profile = AppProfile.Resolve(args, null, null, null, FakeProfile, FakeLocalAppData);

        Assert.AreEqual(AppProfile.DefaultPort, profile.Port);
    }

    [TestMethod]
    public void Resolve_ExplicitDataDir_AlwaysWins()
    {
        var args = new[] { "--profile=4", "--data-dir=D:\\custom\\sr-data" };
        var profile = AppProfile.Resolve(args, null, null, null, FakeProfile, FakeLocalAppData);

        Assert.AreEqual(@"D:\custom\sr-data", profile.DataDirectory);
    }

    [TestMethod]
    public void Resolve_DataDirEnv_UsedWhenArgAbsent()
    {
        var args = new[] { "--profile=4" };
        var profile = AppProfile.Resolve(args, null, null, @"E:\env\sr-data", FakeProfile, FakeLocalAppData);

        Assert.AreEqual(@"E:\env\sr-data", profile.DataDirectory);
    }

    [TestMethod]
    public void Resolve_InvalidProfileName_Ignored()
    {
        var args = new[] { "--profile=bad name!?" };
        var profile = AppProfile.Resolve(args, null, null, null, FakeProfile, FakeLocalAppData);

        Assert.AreEqual("", profile.ProfileName);
        Assert.AreEqual(AppProfile.DefaultPort, profile.Port);
        Assert.AreEqual(@$"{FakeProfile}\.shipright", profile.DataDirectory);
    }
}
