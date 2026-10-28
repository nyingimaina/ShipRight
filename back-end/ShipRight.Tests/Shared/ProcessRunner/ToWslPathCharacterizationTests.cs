using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Shared.ProcessRunner;

namespace ShipRight.Tests.Shared.ProcessRunner;

/// <summary>Pins the current Windows → /mnt/&lt;drive&gt; translation used for in-place WSL builds.</summary>
[TestClass]
public class ToWslPathCharacterizationTests
{
    [TestMethod]
    public void BackslashWindowsPath_BecomesMntPathWithLowercaseDrive()
        => Assert.AreEqual("/mnt/d/work/app", ShipRight.Shared.ProcessRunner.ProcessRunner.ToWslPath(@"D:\work\app"));

    [TestMethod]
    public void ForwardSlashWindowsPath_IsAlsoTranslated()
        => Assert.AreEqual("/mnt/c/Users/me", ShipRight.Shared.ProcessRunner.ProcessRunner.ToWslPath("C:/Users/me"));

    [TestMethod]
    public void DriveRootWithTrailingSlash_YieldsMntDriveSlash()
        => Assert.AreEqual("/mnt/d/", ShipRight.Shared.ProcessRunner.ProcessRunner.ToWslPath(@"D:\"));

    [TestMethod]
    public void LinuxPath_IsUnchanged()
        => Assert.AreEqual("/home/me/app", ShipRight.Shared.ProcessRunner.ProcessRunner.ToWslPath("/home/me/app"));

    [TestMethod]
    public void NonPathArgument_IsUnchanged()
        => Assert.AreEqual("--no-cache", ShipRight.Shared.ProcessRunner.ProcessRunner.ToWslPath("--no-cache"));

    [TestMethod]
    public void DriveLetterWithoutSeparator_IsUnchanged()
        => Assert.AreEqual("D:app", ShipRight.Shared.ProcessRunner.ProcessRunner.ToWslPath("D:app"));

    [TestMethod]
    public void ShortStrings_AreUnchanged()
        => Assert.AreEqual("", ShipRight.Shared.ProcessRunner.ProcessRunner.ToWslPath(""));
}
