using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.JitDeploy;

namespace ShipRight.Tests.Modules.JitDeploy;

/// <summary>Reads the output of <c>docker compose config --format json</c> into the facts a deploy needs.</summary>
[TestClass]
public class ComposeConfigTests
{
    private const string Json = """
    {
      "name": "shop",
      "services": {
        "db":  { "image": "postgres:16" },
        "api": { "build": { "context": "/r/api" }, "image": "shop/api:r42-ab8b4a9", "depends_on": { "db": { "condition": "service_healthy", "required": true } } },
        "web": { "build": { "context": "/r/web" }, "image": "shop/web:r42-ab8b4a9", "depends_on": { "api": { "condition": "service_started" } } }
      }
    }
    """;

    [TestMethod]
    public void Parse_ReadsServicesTheirDependenciesAndWhetherTheyBuild()
    {
        var s = ComposeConfig.Parse(Json).ToDictionary(x => x.Name);
        CollectionAssert.AreEquivalent(new[] { "db", "api", "web" }, s.Keys.ToArray());
        Assert.IsFalse(s["db"].HasBuild);
        Assert.IsTrue(s["api"].HasBuild);
        CollectionAssert.AreEqual(new[] { "db" }, s["api"].DependsOn.ToArray());
        CollectionAssert.AreEqual(new[] { "api" }, s["web"].DependsOn.ToArray());
        Assert.AreEqual("postgres:16", s["db"].Image);
        Assert.AreEqual(0, s["db"].DependsOn.Count);
    }

    [TestMethod]
    public void Parse_AcceptsDependsOnWrittenAsAList()
    {
        var s = ComposeConfig.Parse("""{ "services": { "a": {}, "b": { "depends_on": ["a"] } } }""").ToDictionary(x => x.Name);
        CollectionAssert.AreEqual(new[] { "a" }, s["b"].DependsOn.ToArray());
    }

    [TestMethod]
    public void Parse_SkipsAnyTextBeforeTheJson()
    {
        var s = ComposeConfig.Parse("WARN something\n" + Json);
        Assert.AreEqual(3, s.Count);
    }

    [TestMethod]
    public void Parse_ServiceWithoutImage_HasANullImage()
    {
        var s = ComposeConfig.Parse("""{ "services": { "a": { "build": "." } } }""").Single();
        Assert.IsNull(s.Image);
        Assert.IsTrue(s.HasBuild);
    }

    [DataTestMethod]
    [DataRow("")]
    [DataRow("not json")]
    [DataRow("{}")]
    [DataRow("""{ "services": [] }""")]
    [DataRow("""{ "services": {} }""")]
    public void Parse_OfSomethingThatIsNotAComposeFile_Throws(string json)
    {
        Assert.ThrowsException<ComposeConfigException>(() => ComposeConfig.Parse(json));
    }

    [TestMethod]
    public void Graph_FeedsTheDependencyOrderer()
    {
        var order = DependencyOrderer.StartOrder(ComposeConfig.Graph(ComposeConfig.Parse(Json)));
        CollectionAssert.AreEqual(new[] { "db", "api", "web" }, order.ToArray());
    }

    // ── does the compose file really use the release tag? ────────────────────

    private static IReadOnlyList<BuildExpectation> Expect(params (string Svc, string Var, string Image)[] e) =>
        e.Select(x => new BuildExpectation(x.Svc, x.Var, x.Image)).ToList();

    [TestMethod]
    public void CheckBuilds_AllGood_NoProblems()
    {
        var problems = ComposeConfig.CheckBuilds(ComposeConfig.Parse(Json),
            Expect(("api", "API_TAG", "shop/api:r42-ab8b4a9"), ("web", "WEB_TAG", "shop/web:r42-ab8b4a9")));
        Assert.AreEqual(0, problems.Count);
    }

    [TestMethod]
    public void CheckBuilds_AnImageLineThatIgnoresTheTagVariable_IsExplainedInPlainLanguage()
    {
        var json = """{ "services": { "api": { "build": ".", "image": "shop/api:latest" } } }""";
        var problems = ComposeConfig.CheckBuilds(ComposeConfig.Parse(json), Expect(("api", "API_TAG", "shop/api:r42-ab8b4a9")));
        var p = problems.Single();
        StringAssert.Contains(p, "api");
        StringAssert.Contains(p, "${API_TAG");
        StringAssert.Contains(p, "image:");
    }

    [TestMethod]
    public void CheckBuilds_AServiceWithoutABuildSection_CannotBeBuiltOnTheServer()
    {
        var json = """{ "services": { "api": { "image": "shop/api:r42-ab8b4a9" } } }""";
        var p = ComposeConfig.CheckBuilds(ComposeConfig.Parse(json), Expect(("api", "API_TAG", "shop/api:r42-ab8b4a9"))).Single();
        StringAssert.Contains(p, "build:");
    }

    [TestMethod]
    public void CheckBuilds_AServiceMissingFromTheComposeFile_IsReported()
    {
        var p = ComposeConfig.CheckBuilds(ComposeConfig.Parse(Json), Expect(("ghost", "GHOST_TAG", "shop/ghost:r42-ab8b4a9"))).Single();
        StringAssert.Contains(p, "ghost");
        StringAssert.Contains(p, "not in the compose file");
    }

    [TestMethod]
    public void CheckBuilds_ReportsEveryProblem_NotJustTheFirst()
    {
        var json = """{ "services": { "a": { "build": ".", "image": "x:1" }, "b": { "image": "y:1" } } }""";
        var problems = ComposeConfig.CheckBuilds(ComposeConfig.Parse(json),
            Expect(("a", "A_TAG", "x:r1-aaaaaaa"), ("b", "B_TAG", "y:r1-aaaaaaa"), ("c", "C_TAG", "z:r1-aaaaaaa")));
        Assert.AreEqual(3, problems.Count);
    }
}
