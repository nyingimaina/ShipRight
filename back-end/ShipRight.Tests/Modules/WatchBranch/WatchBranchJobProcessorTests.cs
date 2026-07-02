using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.WatchBranch;

namespace ShipRight.Tests.Modules.WatchBranch;

[TestClass]
public class WatchBranchJobProcessorTests
{
    // ── ParseSha ─────────────────────────────────────────────────────────────

    [TestMethod]
    public void ParseSha_ReturnsFirst40Chars_WhenValidOutput()
    {
        const string sha = "abc1234567890123456789012345678901234567";
        var output = $"{sha}\trefs/heads/master\n";
        Assert.AreEqual(sha, WatchBranchJobProcessor.ParseSha(output));
    }

    [TestMethod]
    public void ParseSha_ReturnsNull_WhenOutputIsEmpty()
    {
        Assert.IsNull(WatchBranchJobProcessor.ParseSha(string.Empty));
    }

    [TestMethod]
    public void ParseSha_ReturnsNull_WhenOutputTooShort()
    {
        Assert.IsNull(WatchBranchJobProcessor.ParseSha("abc123"));
    }

    [TestMethod]
    public void ParseSha_ReturnsNull_WhenOutputIsWhitespace()
    {
        Assert.IsNull(WatchBranchJobProcessor.ParseSha("   "));
    }

    [TestMethod]
    public void ParseSha_HandlesOutputWithoutTrailingNewline()
    {
        const string sha = "abc1234567890123456789012345678901234567";
        var output = $"{sha}\trefs/heads/feature";
        Assert.AreEqual(sha, WatchBranchJobProcessor.ParseSha(output));
    }

    // ── ShouldTriggerBuild ───────────────────────────────────────────────────

    [TestMethod]
    public void ShouldTriggerBuild_ReturnsFalse_WhenShaUnchanged()
    {
        const string sha = "abc1234567890123456789012345678901234567";
        Assert.IsFalse(WatchBranchJobProcessor.ShouldTriggerBuild(sha, sha));
    }

    [TestMethod]
    public void ShouldTriggerBuild_ReturnsTrue_WhenShaChanged()
    {
        const string old = "aaa1234567890123456789012345678901234567";
        const string current = "bbb1234567890123456789012345678901234567";
        Assert.IsTrue(WatchBranchJobProcessor.ShouldTriggerBuild(current, old));
    }

    [TestMethod]
    public void ShouldTriggerBuild_ReturnsFalse_WhenLastShaNull_FirstPoll()
    {
        // First poll: record the SHA but don't trigger — avoids spurious build on startup
        Assert.IsFalse(WatchBranchJobProcessor.ShouldTriggerBuild(
            "abc1234567890123456789012345678901234567", null));
    }
}
