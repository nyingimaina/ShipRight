using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.DockerAdoption;
using ShipRight.Modules.Setup;

namespace ShipRight.Tests.Modules.DockerAdoption;

/// <summary>The person can settle what ShipRight could not (or got differently): a chosen Dockerfile wins. The repository the folder belongs to is reported.</summary>
[TestClass]
public class AdoptionChoicesTests
{
    private string _root = "";

    [TestInitialize] public void Init() { _root = Path.Combine(Path.GetTempPath(), "adopt3-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_root); }
    [TestCleanup] public void Done() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    private void Put(string rel, string content = "")
    {
        var p = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
    }

    private void Ambiguous()
    {
        Put("docker-compose.yml", "services:\n  worker:\n    image: shop/worker:1\n");
        Put("a/worker/Dockerfile", "FROM x\nCOPY . .\n");
        Put("b/worker/Dockerfile", "FROM x\nCOPY . .\n");
    }

    [TestMethod]
    public void AChosenDockerfile_SettlesAnAmbiguousService()
    {
        Ambiguous();
        var s = DockerSetupAnalyzer.Analyse(_root, new Dictionary<string, string> { ["worker"] = "a/worker/Dockerfile" }).Services.Single();

        Assert.AreEqual("Dockerfile", s.DockerfilePath);
        Assert.AreEqual("a/worker", s.ContextRelative);
        Assert.AreEqual(Confidence.High, s.Confidence);
        CollectionAssert.Contains(s.Alternatives.ToArray(), "b/worker/Dockerfile");
        Assert.IsTrue(s.Reasons.Any(r => r.Contains("You chose")));
    }

    [TestMethod]
    public void AChosenDockerfile_OverridesWhatWasPicked()
    {
        Put("docker-compose.yml", "services:\n  backend:\n    image: reg/backend:${IMAGE_TAG:-latest}\n");
        Put("backend.Dockerfile", "FROM x\nCOPY . .\n");
        Put("back-end/dockerfile", "FROM x\nCOPY . ./\n");
        var s = DockerSetupAnalyzer.Analyse(_root, new Dictionary<string, string> { ["backend"] = "back-end/dockerfile" }).Services.Single();

        Assert.AreEqual("dockerfile", s.DockerfilePath);
        Assert.AreEqual("back-end", s.ContextRelative);
        CollectionAssert.Contains(s.Alternatives.ToArray(), "backend.Dockerfile");
    }

    [TestMethod]
    public void AChoiceNamingAFileThatIsNotThere_IsIgnored_WithAWarning()
    {
        Ambiguous();
        var plan = DockerSetupAnalyzer.Analyse(_root, new Dictionary<string, string> { ["worker"] = "nope/Dockerfile" });
        Assert.IsNull(plan.Services.Single().DockerfilePath, "still undecided");
        Assert.IsTrue(plan.Warnings.Any(w => w.Contains("nope/Dockerfile")));
    }

    [TestMethod]
    public void WithNoChoices_NothingChanges()
    {
        Ambiguous();
        Assert.IsNull(DockerSetupAnalyzer.Analyse(_root, null).Services.Single().DockerfilePath);
        Assert.IsNull(DockerSetupAnalyzer.Analyse(_root, new Dictionary<string, string>()).Services.Single().DockerfilePath);
    }

    // ── the repository ───────────────────────────────────────────────────────

    [TestMethod]
    public void TheGitRepositoryAndBranch_AreReported()
    {
        Put("Dockerfile", "FROM x\n");
        Put(".git/HEAD", "ref: refs/heads/release\n");
        var plan = DockerSetupAnalyzer.Analyse(_root);
        Assert.AreEqual(_root, plan.RepoRoot);
        Assert.AreEqual("release", plan.RepoBranch);
    }

    [TestMethod]
    public void AFolderInsideALargerRepository_UsesThatRepository()
    {
        Put(".git/HEAD", "ref: refs/heads/main\n");
        Put("apps/web/Dockerfile", "FROM x\n");
        var plan = DockerSetupAnalyzer.Analyse(Path.Combine(_root, "apps", "web"));
        Assert.AreEqual(_root, plan.RepoRoot);
        Assert.AreEqual("main", plan.RepoBranch);
    }

    [TestMethod]
    public void NoGit_MeansNoBranch_AndTheFolderItself()
    {
        Put("Dockerfile", "FROM x\n");
        var plan = DockerSetupAnalyzer.Analyse(_root);
        Assert.IsNull(plan.RepoBranch);
        Assert.IsNotNull(plan.RepoRoot);
    }

    [TestMethod]
    public void ADetachedHead_HasNoBranch()
    {
        Put("Dockerfile", "FROM x\n");
        Put(".git/HEAD", "0123456789abcdef0123456789abcdef01234567\n");
        Assert.IsNull(DockerSetupAnalyzer.Analyse(_root).RepoBranch);
    }

    // ── the endpoints carry the choices ──────────────────────────────────────

    private static int Status(IResult r) => ((IStatusCodeHttpResult)r).StatusCode ?? 200;

    [TestMethod]
    public void ThePlanEndpoint_AppliesTheChoices()
    {
        Ambiguous();
        var r = AdoptionRoutes.HandlePlan(new AdoptRequest(_root, null, new Dictionary<string, string> { ["worker"] = "b/worker/Dockerfile" }));
        Assert.AreEqual(200, Status(r));
        StringAssert.Contains(JsonSerializer.Serialize(((IValueHttpResult)r).Value, new JsonSerializerOptions(JsonSerializerDefaults.Web)), "\"contextRelative\":\"b/worker\"");
    }

    [TestMethod]
    public void ThePreviewAndApply_UseTheSameChoices()
    {
        Ambiguous();
        var choices = new Dictionary<string, string> { ["worker"] = "a/worker/Dockerfile" };
        var r = AdoptionRoutes.HandleComposePreview(new AdoptRequest(_root, "registry", choices));
        StringAssert.Contains(JsonSerializer.Serialize(((IValueHttpResult)r).Value, new JsonSerializerOptions(JsonSerializerDefaults.Web)), "WORKER_TAG");
        var applied = AdoptionRoutes.HandleComposeApply(new AdoptRequest(_root, "registry", choices));
        Assert.AreEqual(200, Status(applied));
        StringAssert.Contains(File.ReadAllText(Path.Combine(_root, "docker-compose.yml")), "${WORKER_TAG:-shop/worker:1}");
    }
}
