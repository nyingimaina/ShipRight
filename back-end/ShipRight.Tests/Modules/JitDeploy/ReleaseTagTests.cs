using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.JitDeploy;

namespace ShipRight.Tests.Modules.JitDeploy;

[TestClass]
public class ReleaseTagTests
{
    [TestMethod]
    public void Format_IsCounterThenShortSha()
    {
        Assert.AreEqual("r42-ab8b4a9", ReleaseTag.Format(42, "ab8b4a9f3c1d2e5f6a7b8c9d0e1f2a3b4c5d6e7f"));
    }

    [TestMethod]
    public void Format_LowerCasesAndTrimsTheSha()
    {
        Assert.AreEqual("r1-abcdef1", ReleaseTag.Format(1, "  ABCDEF1234  "));
    }

    [TestMethod]
    public void Format_IsAValidDockerTag()
    {
        Assert.IsTrue(System.Text.RegularExpressions.Regex.IsMatch(ReleaseTag.Format(7, "0123456789abcdef"), @"^[a-z0-9][a-z0-9_.-]{0,127}$"));
    }

    [DataTestMethod]
    [DataRow(0, "abcdef1")]
    [DataRow(-3, "abcdef1")]
    [DataRow(1, "abc")]          // too short to identify a commit
    [DataRow(1, "not-a-sha!!")]
    [DataRow(1, "")]
    public void Format_RejectsAnInvalidCounterOrSha(int counter, string sha)
    {
        Assert.ThrowsException<ArgumentException>(() => ReleaseTag.Format(counter, sha));
    }

    [TestMethod]
    public void TryParse_ReadsBackWhatFormatWrote()
    {
        Assert.IsTrue(ReleaseTag.TryParse("r42-ab8b4a9", out var counter, out var sha));
        Assert.AreEqual(42, counter);
        Assert.AreEqual("ab8b4a9", sha);
    }

    [DataTestMethod]
    [DataRow("latest")]
    [DataRow("1.2.3")]
    [DataRow("r-ab8b4a9")]
    [DataRow("r42")]
    [DataRow("r42-xyz")]
    [DataRow("R42-ab8b4a9")]
    [DataRow("r0-ab8b4a9")]
    [DataRow("")]
    public void TryParse_RejectsTagsThatAreNotReleaseTags(string tag)
    {
        Assert.IsFalse(ReleaseTag.TryParse(tag, out _, out _));
    }

    [TestMethod]
    public void NextCounter_IsOneAfterTheHighest_IgnoringOtherTags()
    {
        Assert.AreEqual(1, ReleaseTag.NextCounter([]));
        Assert.AreEqual(1, ReleaseTag.NextCounter(["latest", "1.0.0"]));
        Assert.AreEqual(12, ReleaseTag.NextCounter(["r3-aaaaaaa", "r11-bbbbbbb", "latest", "r9-ccccccc"]));
    }
}
