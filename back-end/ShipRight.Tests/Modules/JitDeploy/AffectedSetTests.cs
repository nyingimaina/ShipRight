using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.JitDeploy;

namespace ShipRight.Tests.Modules.JitDeploy;

[TestClass]
public class AffectedSetTests
{
    private static IReadOnlyDictionary<string, IReadOnlyList<string>> G(params (string Svc, string[] Deps)[] e) =>
        e.ToDictionary(x => x.Svc, x => (IReadOnlyList<string>)x.Deps);

    // db <- api <- web ; cache is independent
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> Stack =
        G(("web", ["api"]), ("api", ["db"]), ("db", []), ("cache", []));

    private static string[] Names(IReadOnlyList<AffectedService> a) => a.Select(x => x.Name).ToArray();

    [TestMethod]
    public void ReplacedServices_AreAlwaysIncluded_WithTheirReason()
    {
        var r = AffectedSet.Compute(Stack, ["cache"]);
        CollectionAssert.AreEqual(new[] { "cache" }, Names(r));
        Assert.AreEqual("its image was replaced", r[0].Reason);
    }

    [TestMethod]
    public void Dependents_AreIncludedByDefault_Transitively()
    {
        var r = AffectedSet.Compute(Stack, ["db"]);
        CollectionAssert.AreEqual(new[] { "db", "api", "web" }, Names(r));
    }

    [TestMethod]
    public void TheDefaultIsRestart_SoAnUnansweredPairIsTheSafeChoice()
    {
        var r = AffectedSet.Compute(Stack, ["api"], choices: new Dictionary<(string, string), bool>());
        CollectionAssert.AreEqual(new[] { "api", "web" }, Names(r));
    }

    [TestMethod]
    public void ALeaveRunningChoice_StopsThePropagation_AtThatPair()
    {
        var choices = new Dictionary<(string, string), bool> { [("api", "web")] = false };
        var r = AffectedSet.Compute(Stack, ["api"], choices);
        CollectionAssert.AreEqual(new[] { "api" }, Names(r));
    }

    [TestMethod]
    public void AChoiceOnALaterPair_OnlyCutsThatBranch()
    {
        var choices = new Dictionary<(string, string), bool> { [("api", "web")] = false };
        var r = AffectedSet.Compute(Stack, ["db"], choices);
        CollectionAssert.AreEqual(new[] { "db", "api" }, Names(r));
    }

    [TestMethod]
    public void ADependentThatIsItselfReplaced_IsIncludedEvenWhenTheChoiceIsLeaveRunning()
    {
        var choices = new Dictionary<(string, string), bool> { [("db", "api")] = false };
        var r = AffectedSet.Compute(Stack, ["db", "api"], choices);
        Assert.IsTrue(Names(r).Contains("api"));
        Assert.AreEqual("its image was replaced", r.Single(x => x.Name == "api").Reason);
    }

    [TestMethod]
    public void TheResult_IsInStartOrder()
    {
        var r = AffectedSet.Compute(Stack, ["web", "db"]);
        CollectionAssert.AreEqual(new[] { "db", "api", "web" }, Names(r));
    }

    [TestMethod]
    public void ARestartedDependent_SaysWhichServiceCausedIt()
    {
        var r = AffectedSet.Compute(Stack, ["db"]);
        Assert.AreEqual("it depends on api, which is being restarted", r.Single(x => x.Name == "web").Reason);
        Assert.AreEqual("it depends on db, which is being restarted", r.Single(x => x.Name == "api").Reason);
    }

    [TestMethod]
    public void UnrelatedServices_AreNeverTouched()
    {
        var r = AffectedSet.Compute(Stack, ["api"]);
        Assert.IsFalse(Names(r).Contains("cache"));
        Assert.IsFalse(Names(r).Contains("db"));
    }

    [TestMethod]
    public void AReplacedServiceNotInTheGraph_Throws()
    {
        Assert.ThrowsException<ArgumentException>(() => AffectedSet.Compute(Stack, ["ghost"]));
    }

    [TestMethod]
    public void NothingReplaced_MeansNothingAffected()
    {
        Assert.AreEqual(0, AffectedSet.Compute(Stack, []).Count);
    }
}
