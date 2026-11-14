using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.JitDeploy;

namespace ShipRight.Tests.Modules.JitDeploy;

[TestClass]
public class ReleaseRetentionTests
{
    private static ReleaseInfo R(int n, params string[] images) =>
        new(ReleaseTag.Format(n, "abcdef1"), images.Length > 0 ? images : [$"app/api:r{n}"]);

    private static string T(int n) => ReleaseTag.Format(n, "abcdef1");
    private static readonly IReadOnlySet<string> NoneRunning = new HashSet<string>();

    private static string[] Prune(IEnumerable<ReleaseInfo> releases, int keep, int? current = null, int? previous = null, IReadOnlySet<string>? running = null) =>
        ReleaseRetention.SelectToPrune(releases.ToList(), keep,
            current is null ? null : T(current.Value), previous is null ? null : T(previous.Value), running ?? NoneRunning).ToArray();

    [TestMethod]
    public void KeepsTheNewestN_PrunesTheRest_OldestFirst()
    {
        var pruned = Prune([R(1), R(2), R(3), R(4), R(5)], keep: 3, current: 5, previous: 4);
        CollectionAssert.AreEqual(new[] { T(1), T(2) }, pruned);
    }

    [TestMethod]
    public void NothingToPrune_WhenWithinTheLimit()
    {
        Assert.AreEqual(0, Prune([R(1), R(2)], keep: 3, current: 2, previous: 1).Length);
    }

    [TestMethod]
    public void TheMinimumIsTwo_EvenWhenAskedForLess()
    {
        Assert.AreEqual(2, ReleaseRetention.MinimumToKeep);
        var pruned = Prune([R(1), R(2), R(3)], keep: 0, current: 3, previous: 2);
        CollectionAssert.AreEqual(new[] { T(1) }, pruned);
        pruned = Prune([R(1), R(2), R(3)], keep: 1, current: 3, previous: 2);
        CollectionAssert.AreEqual(new[] { T(1) }, pruned);
    }

    [TestMethod]
    public void TheCurrentAndPreviousReleases_ArePreservedEvenWhenOld()
    {
        // After a rollback the current release can be older than the newest ones.
        var pruned = Prune([R(1), R(2), R(3), R(4), R(5), R(6)], keep: 2, current: 2, previous: 1);
        CollectionAssert.DoesNotContain(pruned, T(1));
        CollectionAssert.DoesNotContain(pruned, T(2));
        CollectionAssert.AreEquivalent(new[] { T(3), T(4) }, pruned);   // 5 and 6 are the newest two
    }

    [TestMethod]
    public void AReleaseWithARunningImage_IsNeverPruned()
    {
        var running = new HashSet<string> { "app/api:r1" };
        var pruned = Prune([R(1), R(2), R(3), R(4)], keep: 2, current: 4, previous: 3, running: running);
        CollectionAssert.AreEqual(new[] { T(2) }, pruned);
    }

    [TestMethod]
    public void ARunningImageMatchIsExact_NotAPrefix()
    {
        var running = new HashSet<string> { "app/api:r10" };
        var pruned = Prune([R(1), R(2), R(3), R(4)], keep: 2, current: 4, previous: 3, running: running);
        CollectionAssert.AreEqual(new[] { T(1), T(2) }, pruned);
    }

    [TestMethod]
    public void PrunesWholeReleases_NotIndividualImages()
    {
        var pruned = Prune([R(1, "a/api:r1", "a/web:r1"), R(2), R(3)], keep: 2, current: 3, previous: 2);
        CollectionAssert.AreEqual(new[] { T(1) }, pruned);
    }

    [TestMethod]
    public void TagsThatAreNotReleaseTags_AreNeverReturned()
    {
        var pruned = Prune([new ReleaseInfo("latest", ["a:latest"]), new ReleaseInfo("1.0.0", ["a:1.0.0"]), R(1), R(2), R(3)], keep: 2, current: 3, previous: 2);
        CollectionAssert.AreEqual(new[] { T(1) }, pruned);
    }

    [TestMethod]
    public void NoCurrentRelease_StillKeepsTheNewestN()
    {
        var pruned = Prune([R(1), R(2), R(3), R(4)], keep: 2);
        CollectionAssert.AreEqual(new[] { T(1), T(2) }, pruned);
    }

    [TestMethod]
    public void AnEmptyList_PrunesNothing()
    {
        Assert.AreEqual(0, Prune([], keep: 3).Length);
    }

    [TestMethod]
    public void TheResult_IsOrderedOldestFirst_RegardlessOfInputOrder()
    {
        var pruned = Prune([R(4), R(1), R(3), R(2), R(5)], keep: 2, current: 5, previous: 4);
        CollectionAssert.AreEqual(new[] { T(1), T(2), T(3) }, pruned);
    }

    [TestMethod]
    public void OrderingIsNumeric_NotAlphabetical()
    {
        var pruned = Prune([R(9), R(10), R(11)], keep: 2, current: 11, previous: 10);
        CollectionAssert.AreEqual(new[] { T(9) }, pruned);
    }
}
