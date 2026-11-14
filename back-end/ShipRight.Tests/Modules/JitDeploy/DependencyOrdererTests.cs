using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.JitDeploy;

namespace ShipRight.Tests.Modules.JitDeploy;

[TestClass]
public class DependencyOrdererTests
{
    private static IReadOnlyDictionary<string, IReadOnlyList<string>> G(params (string Svc, string[] Deps)[] e) =>
        e.ToDictionary(x => x.Svc, x => (IReadOnlyList<string>)x.Deps);

    [TestMethod]
    public void StartOrder_PutsDependenciesBeforeDependents()
    {
        var g = G(("web", ["api"]), ("api", ["db"]), ("db", []));
        CollectionAssert.AreEqual(new[] { "db", "api", "web" }, DependencyOrderer.StartOrder(g).ToArray());
    }

    [TestMethod]
    public void StopOrder_IsTheExactReverse_DependentsFirst()
    {
        var g = G(("web", ["api"]), ("api", ["db"]), ("db", []));
        CollectionAssert.AreEqual(new[] { "web", "api", "db" }, DependencyOrderer.StopOrder(g).ToArray());
    }

    [TestMethod]
    public void IndependentServices_AreOrderedAlphabetically_SoRunsAreRepeatable()
    {
        var g = G(("zeta", []), ("alpha", []), ("mid", []));
        CollectionAssert.AreEqual(new[] { "alpha", "mid", "zeta" }, DependencyOrderer.StartOrder(g).ToArray());
    }

    [TestMethod]
    public void ADiamond_StartsTheSharedDependencyFirstAndTheJoinLast()
    {
        var g = G(("app", ["left", "right"]), ("left", ["db"]), ("right", ["db"]), ("db", []));
        CollectionAssert.AreEqual(new[] { "db", "left", "right", "app" }, DependencyOrderer.StartOrder(g).ToArray());
    }

    [TestMethod]
    public void AnEmptyGraph_GivesAnEmptyOrder()
    {
        Assert.AreEqual(0, DependencyOrderer.StartOrder(G()).Count);
    }

    [TestMethod]
    public void ACycle_ThrowsAndNamesTheServicesInIt()
    {
        var g = G(("a", ["b"]), ("b", ["c"]), ("c", ["a"]), ("ok", []));
        var ex = Assert.ThrowsException<DependencyCycleException>(() => DependencyOrderer.StartOrder(g));
        CollectionAssert.AreEquivalent(new[] { "a", "b", "c" }, ex.Cycle.Distinct().ToArray());
        StringAssert.Contains(ex.Message, "a");
        StringAssert.Contains(ex.Message, "circle");
    }

    [TestMethod]
    public void ASelfDependency_IsACycle()
    {
        var g = G(("a", ["a"]));
        Assert.ThrowsException<DependencyCycleException>(() => DependencyOrderer.StartOrder(g));
    }

    [TestMethod]
    public void ADependencyOnAnUndefinedService_Throws_NamingBoth()
    {
        var g = G(("web", ["ghost"]));
        var ex = Assert.ThrowsException<UnknownDependencyException>(() => DependencyOrderer.StartOrder(g));
        Assert.AreEqual("web", ex.Service);
        Assert.AreEqual("ghost", ex.Dependency);
    }

    [TestMethod]
    public void ASubset_KeepsTheRelativeOrderOfTheFullGraph()
    {
        var g = G(("web", ["api"]), ("api", ["db"]), ("db", []), ("cache", []));
        CollectionAssert.AreEqual(new[] { "api", "web" }, DependencyOrderer.StartOrder(g, ["web", "api"]).ToArray());
        CollectionAssert.AreEqual(new[] { "web", "api" }, DependencyOrderer.StopOrder(g, ["web", "api"]).ToArray());
    }

    [TestMethod]
    public void ASubsetNamingAnUnknownService_Throws()
    {
        Assert.ThrowsException<ArgumentException>(() => DependencyOrderer.StartOrder(G(("a", [])), ["nope"]));
    }
}
