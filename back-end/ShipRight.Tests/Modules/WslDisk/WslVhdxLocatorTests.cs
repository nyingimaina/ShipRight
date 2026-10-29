using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.System.WslDisk;

namespace ShipRight.Tests.WslDisk;

[TestClass]
public class WslVhdxLocatorTests
{
    private static string Temp()
    {
        var dir = Path.Combine(Path.GetTempPath(), "shipright-vhdx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string Disk(string dir, string name = "ext4.vhdx")
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, new byte[16]);
        return path;
    }

    [TestMethod]
    public void AFolderRegisteredByWsl_IsUsed_EvenWhenItIsNotUnderPackages()
    {
        var root = Temp();
        try
        {
            var disk = Disk(Path.Combine(root, "D-drive", "ubuntu"));
            var locator = new WslVhdxLocator(() => [(Path.Combine(root, "D-drive", "ubuntu"), null)], []);

            CollectionAssert.AreEqual(new[] { disk }, locator.Find().ToArray());
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void ARegisteredDiskFileName_IsHonoured_AndTheWindowsExtendedPathPrefixIsIgnored()
    {
        var root = Temp();
        try
        {
            var disk = Disk(Path.Combine(root, "arch"), "custom.vhdx");
            var locator = new WslVhdxLocator(() => [(@"\\?\" + Path.Combine(root, "arch"), "custom.vhdx")], []);

            CollectionAssert.AreEqual(new[] { disk }, locator.Find().ToArray());
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void DistrosInstalledWithoutAStorePackage_AreFoundUnderTheWslFolder()
    {
        var root = Temp();
        try
        {
            var disk = Disk(Path.Combine(root, "wsl", "{3f2a-9c1d}"));
            var locator = new WslVhdxLocator(() => [], [Path.Combine(root, "wsl")]);

            CollectionAssert.AreEqual(new[] { disk }, locator.Find().ToArray());
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void TheSameDiskFoundTwoWays_IsListedOnce()
    {
        var root = Temp();
        try
        {
            var dir = Path.Combine(root, "wsl", "abc");
            var disk = Disk(dir);
            var locator = new WslVhdxLocator(() => [(dir, null), (dir.ToUpperInvariant(), null)], [Path.Combine(root, "wsl")]);

            Assert.AreEqual(1, locator.Find().Count);
            Assert.AreEqual(disk, locator.Find()[0]);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void RegisteredFolders_WithoutAFile_AreIgnored_AndNothingThrows()
    {
        var root = Temp();
        try
        {
            var locator = new WslVhdxLocator(() => [(Path.Combine(root, "gone"), null), ("", null)], [Path.Combine(root, "nope")]);

            Assert.AreEqual(0, locator.Find().Count);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void AFailingRegistryRead_DoesNotStopTheFolderSearch()
    {
        var root = Temp();
        try
        {
            var disk = Disk(Path.Combine(root, "wsl", "abc"));
            var locator = new WslVhdxLocator(() => throw new UnauthorizedAccessException(), [Path.Combine(root, "wsl")]);

            CollectionAssert.AreEqual(new[] { disk }, locator.Find().ToArray());
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void ReadingTheRealRegistry_NeverThrows_AndOnlyReturnsFilesThatExist()
    {
        var found = new WslVhdxLocator().Find();

        Assert.IsTrue(found.All(File.Exists));
        Assert.AreEqual(found.Count, found.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        if (Environment.GetEnvironmentVariable("SHIPRIGHT_PRINT_VHDX") == "1")
            Console.WriteLine("VHDX: " + string.Join(" | ", found));
    }

    [TestMethod]
    public void TheSearchedPlaces_AreAvailableForAHelpfulMessage()
    {
        var locator = new WslVhdxLocator(() => [(@"C:\wsl\ubuntu", null)], [@"C:\Users\x\AppData\Local\wsl"]);

        var places = locator.SearchedPlaces();

        Assert.IsTrue(places.Any(p => p.Contains(@"C:\wsl\ubuntu")));
        Assert.IsTrue(places.Any(p => p.Contains(@"AppData\Local\wsl")));
    }
}
