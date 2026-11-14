using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.DockerAdoption;

namespace ShipRight.Tests.Modules.DockerAdoption;

/// <summary>Version files for adopted services, the proposed compose changes (shown before anything is written) and applying them.</summary>
[TestClass]
public class AdoptionApplyTests
{
    private string _root = "";

    [TestInitialize] public void Init() { _root = Path.Combine(Path.GetTempPath(), "adopt2-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_root); }
    [TestCleanup] public void Done() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    private string Put(string rel, string content = "")
    {
        var p = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
        return p;
    }

    private string Read(string rel) => File.ReadAllText(Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar)));

    private const string Compose = "x-logging: &l\n  driver: json-file\n\nservices:\n  db:\n    image: mariadb:11.4\n    restart: always\n  backend:\n    image: 825347768469.dkr.ecr.eu-north-1.amazonaws.com/savanna-herdiq-backend:${IMAGE_TAG:-latest}\n    restart: always  # keep it up\n    depends_on:\n      - db\n  frontend:\n    image: \"825347768469.dkr.ecr.eu-north-1.amazonaws.com/savanna-herdiq-frontend:${IMAGE_TAG:-latest}\"\n    depends_on:\n      - backend\nvolumes:\n  data:\n";
    private const string BackendDf = "FROM x AS build\nCOPY back-end/app.csproj back-end/\nCOPY . .\nFROM y\nCOPY --from=build /a /a\n";
    private const string FrontendDf = "FROM x\nARG NEXT_PUBLIC_API_URL\nCOPY front-end/package.json ./\n";

    private void Savanna(bool versions)
    {
        Put("docker-compose.yml", Compose);
        Put("backend.Dockerfile", BackendDf);
        Put("frontend.Dockerfile", FrontendDf);
        Put("back-end/app.csproj", "<Project/>");
        Put("front-end/package.json", "{}");
        if (versions) { Put("back-end/version.txt", "1.4.0\n"); Put("front-end/version.txt", "2.0.1\n"); }
    }

    // ── version files ────────────────────────────────────────────────────────

    [TestMethod]
    public void AnExistingVersionFile_IsFoundNextToTheAppItBuilds()
    {
        Savanna(versions: true);
        var plan = DockerSetupAnalyzer.Analyse(_root);
        Assert.AreEqual("back-end/version.txt", plan.Services.Single(s => s.Name == "backend").VersionFile);
        Assert.AreEqual("front-end/version.txt", plan.Services.Single(s => s.Name == "frontend").VersionFile);
    }

    [TestMethod]
    public void WithNoVersionFile_OneIsSuggestedInTheAppFolder_NotCreated()
    {
        Savanna(versions: false);
        var s = DockerSetupAnalyzer.Analyse(_root).Services.Single(x => x.Name == "backend");
        Assert.IsNull(s.VersionFile);
        Assert.AreEqual("back-end/version.txt", s.SuggestedVersionFile);
        Assert.IsFalse(File.Exists(Path.Combine(_root, "back-end", "version.txt")));
    }

    [TestMethod]
    public void ASimpleService_SuggestsItsOwnFolder()
    {
        Put("api/Dockerfile", "FROM x\nCOPY package.json ./\n");
        Put("api/package.json", "{}");
        Assert.AreEqual("api/version.txt", DockerSetupAnalyzer.Analyse(_root).Services.Single().SuggestedVersionFile);
    }

    [TestMethod]
    public void CreatingVersionFiles_IsCreateOnly_AndStaysInsideTheFolder()
    {
        Savanna(versions: false);
        Put("front-end/version.txt", "9.9.9\n");
        var r = AdoptionApply.CreateVersionFiles(_root, ["backend", "frontend", "nope"]);

        Assert.AreEqual("0.1.0\n", Read("back-end/version.txt"));
        Assert.AreEqual("9.9.9\n", Read("front-end/version.txt"), "an existing file is never overwritten");
        CollectionAssert.AreEqual(new[] { "back-end/version.txt" }, r.Created.ToArray());
        CollectionAssert.AreEqual(new[] { "front-end/version.txt" }, r.AlreadyThere.ToArray());
        CollectionAssert.AreEqual(new[] { "nope" }, r.Unknown.ToArray());
    }

    // ── the compose changes, proposed ────────────────────────────────────────

    private static string[] Lines(string t) => t.Replace("\r\n", "\n").Split('\n');

    [TestMethod]
    public void RegistryMode_GivesEachBuiltServiceItsOwnTagVariable_AndTouchesNothingElse()
    {
        Savanna(true);
        var p = ComposeAdvice.Propose(_root, DockerSetupAnalyzer.Analyse(_root), ComposeMode.Registry);

        var before = Lines(Compose); var after = Lines(p.NewText);
        Assert.AreEqual(before.Length, after.Length);
        var changed = Enumerable.Range(0, before.Length).Where(i => before[i] != after[i]).ToList();
        Assert.AreEqual(2, changed.Count, string.Join("\n", changed.Select(i => after[i])));
        Assert.AreEqual("    image: ${BACKEND_TAG:-825347768469.dkr.ecr.eu-north-1.amazonaws.com/savanna-herdiq-backend:latest}", after[changed[0]]);
        Assert.AreEqual("    image: ${FRONTEND_TAG:-825347768469.dkr.ecr.eu-north-1.amazonaws.com/savanna-herdiq-frontend:latest}", after[changed[1]]);
        StringAssert.Contains(after[9], "# keep it up", "comments on other lines survive");
        Assert.AreEqual(2, p.Changes.Count);
        Assert.IsTrue(p.Changes.All(c => c.Kind == "image"));
    }

    [TestMethod]
    public void ThirdPartyServices_AreNeverTouched()
    {
        Savanna(true);
        var p = ComposeAdvice.Propose(_root, DockerSetupAnalyzer.Analyse(_root), ComposeMode.Registry);
        StringAssert.Contains(p.NewText, "image: mariadb:11.4");
    }

    [TestMethod]
    public void TheCurrentDefaultTagIsKept()
    {
        Put("docker-compose.yml", "services:\n  api:\n    image: shop/api:1.2.3\n");
        Put("api/Dockerfile", "FROM x\n");
        var p = ComposeAdvice.Propose(_root, DockerSetupAnalyzer.Analyse(_root), ComposeMode.Registry);
        StringAssert.Contains(p.NewText, "image: ${API_TAG:-shop/api:1.2.3}");
    }

    [TestMethod]
    public void AnImageThatAlreadyHasItsOwnVariable_IsLeftAlone()
    {
        Put("docker-compose.yml", "services:\n  api:\n    image: ${API_TAG:-shop/api:latest}\n");
        Put("api/Dockerfile", "FROM x\n");
        var p = ComposeAdvice.Propose(_root, DockerSetupAnalyzer.Analyse(_root), ComposeMode.Registry);
        Assert.AreEqual(0, p.Changes.Count);
        Assert.AreEqual("services:\n  api:\n    image: ${API_TAG:-shop/api:latest}\n", p.NewText);
    }

    [TestMethod]
    public void BuildOnServerMode_AddsABuildSectionWithTheRecipe_BeforeTheImageLine()
    {
        Put("docker-compose.yml", "services:\n  api:\n    image: shop/api\n    restart: always\n  db:\n    image: mariadb:11\n");
        Put("svc/Dockerfile.prod", "FROM x AS build\nFROM x AS runner\nARG API_URL\n");
        Put("docker-compose.yml", "services:\n  api:\n    image: shop/api\n    build:\n      context: ./svc\n      dockerfile: Dockerfile.prod\n      target: runner\n      args:\n        API_URL: \"https://x.example\"\n");
        // a service with its own build section keeps it
        var plan = DockerSetupAnalyzer.Analyse(_root);
        var p = ComposeAdvice.Propose(_root, plan, ComposeMode.BuildOnServer);
        StringAssert.Contains(p.NewText, "context: ./svc");
        Assert.IsTrue(p.Notes.Any(n => n.Contains("already has a build section")));
        Assert.IsFalse(p.Changes.Any(c => c.Kind == "build"));
    }

    [TestMethod]
    public void BuildOnServerMode_WritesTheRecipeIntoAServiceWithoutOne()
    {
        Savanna(true);
        var plan = DockerSetupAnalyzer.Analyse(_root);
        var p = ComposeAdvice.Propose(_root, plan, ComposeMode.BuildOnServer);
        var text = p.NewText.Replace("\r\n", "\n");

        StringAssert.Contains(text, "  backend:\n    build:\n      context: .\n      dockerfile: backend.Dockerfile\n    image: ${BACKEND_TAG:-");
        StringAssert.Contains(text, "  frontend:\n    build:\n      context: .\n      dockerfile: frontend.Dockerfile\n      args:\n        NEXT_PUBLIC_API_URL: \"\"\n    image: ${FRONTEND_TAG:-");
        Assert.AreEqual(2, p.Changes.Count(c => c.Kind == "build"));
        Assert.IsTrue(p.Notes.Any(n => n.Contains("NEXT_PUBLIC_API_URL")), "a build argument with no value is called out");
    }

    [TestMethod]
    public void TheContextIsWrittenRelativeToTheComposeFile()
    {
        Put("deploy/docker-compose.yml", "services:\n  api:\n    image: shop/api\n");
        Put("api/Dockerfile", "FROM x\nCOPY package.json ./\n");
        Put("api/package.json", "{}");
        var p = ComposeAdvice.Propose(_root, DockerSetupAnalyzer.Analyse(_root), ComposeMode.BuildOnServer);
        StringAssert.Contains(p.NewText.Replace("\r\n", "\n"), "      context: ../api\n      dockerfile: Dockerfile\n");
    }

    [TestMethod]
    public void AnAmbiguousService_IsSkipped_WithANote()
    {
        Put("docker-compose.yml", "services:\n  worker:\n    image: shop/worker:1\n");
        Put("a/worker/Dockerfile", "FROM x\n");
        Put("b/worker/Dockerfile", "FROM x\n");
        var p = ComposeAdvice.Propose(_root, DockerSetupAnalyzer.Analyse(_root), ComposeMode.Registry);
        Assert.AreEqual(0, p.Changes.Count);
        Assert.IsTrue(p.Notes.Any(n => n.Contains("worker") && n.Contains("skipped")));
    }

    [TestMethod]
    public void WindowsLineEndings_AreKept()
    {
        Put("docker-compose.yml", "services:\r\n  api:\r\n    image: shop/api:1\r\n");
        Put("api/Dockerfile", "FROM x\n");
        var p = ComposeAdvice.Propose(_root, DockerSetupAnalyzer.Analyse(_root), ComposeMode.Registry);
        Assert.AreEqual("services:\r\n  api:\r\n    image: ${API_TAG:-shop/api:1}\r\n", p.NewText);
    }

    [TestMethod]
    public void NoComposeFile_ProposesNothing()
    {
        Put("api/Dockerfile", "FROM x\n");
        var p = ComposeAdvice.Propose(_root, DockerSetupAnalyzer.Analyse(_root), ComposeMode.Registry);
        Assert.AreEqual(0, p.Changes.Count);
        Assert.IsTrue(p.Notes.Any(n => n.Contains("no compose file")));
    }

    // ── applying them ────────────────────────────────────────────────────────

    [TestMethod]
    public void Applying_KeepsABackup_AndWritesTheProposal()
    {
        Savanna(true);
        var r = AdoptionApply.ApplyCompose(_root, ComposeMode.Registry);

        Assert.IsTrue(r.Written);
        Assert.AreEqual(Compose, Read("docker-compose.yml.shipright-backup"));
        StringAssert.Contains(Read("docker-compose.yml"), "${BACKEND_TAG:-");
        Assert.AreEqual("docker-compose.yml.shipright-backup", r.BackupFile);
    }

    [TestMethod]
    public void ApplyingAgain_ChangesNothing_AndKeepsTheFirstBackup()
    {
        Savanna(true);
        AdoptionApply.ApplyCompose(_root, ComposeMode.Registry);
        var after = Read("docker-compose.yml");
        var second = AdoptionApply.ApplyCompose(_root, ComposeMode.Registry);

        Assert.IsFalse(second.Written);
        Assert.AreEqual(after, Read("docker-compose.yml"));
        Assert.AreEqual(Compose, Read("docker-compose.yml.shipright-backup"), "the original is never replaced by a later backup");
    }

    [TestMethod]
    public void Applying_WithNothingToChange_WritesNoFiles()
    {
        Put("docker-compose.yml", "services:\n  db:\n    image: mariadb:11\n");
        var r = AdoptionApply.ApplyCompose(_root, ComposeMode.Registry);
        Assert.IsFalse(r.Written);
        Assert.IsFalse(File.Exists(Path.Combine(_root, "docker-compose.yml.shipright-backup")));
    }

    // ── the endpoints ────────────────────────────────────────────────────────

    private static int Status(IResult r) => ((IStatusCodeHttpResult)r).StatusCode ?? 200;
    private static string Json(IResult r) => JsonSerializer.Serialize(((IValueHttpResult)r).Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    [TestMethod]
    public void ThePlanEndpoint_ReturnsTheServicesAndWhatIsLeftAlone()
    {
        Savanna(true);
        var r = AdoptionRoutes.HandlePlan(new AdoptRequest(_root, null));
        Assert.AreEqual(200, Status(r));
        var json = Json(r);
        StringAssert.Contains(json, "backend.Dockerfile");
        StringAssert.Contains(json, "thirdParty");
        StringAssert.Contains(json, "back-end/version.txt");
    }

    [TestMethod]
    public void TheEndpoints_RefuseAMissingFolder_Plainly()
    {
        Assert.AreEqual(400, Status(AdoptionRoutes.HandlePlan(new AdoptRequest("", null))));
        Assert.AreEqual(400, Status(AdoptionRoutes.HandlePlan(new AdoptRequest(Path.Combine(_root, "nope"), null))));
        Assert.AreEqual(400, Status(AdoptionRoutes.HandleComposePreview(new AdoptRequest(_root, "sideways"))));
    }

    [TestMethod]
    public void ThePreviewEndpoint_ShowsTheChanges_WithoutWriting()
    {
        Savanna(true);
        var r = AdoptionRoutes.HandleComposePreview(new AdoptRequest(_root, "registry"));
        Assert.AreEqual(200, Status(r));
        StringAssert.Contains(Json(r), "BACKEND_TAG");
        Assert.AreEqual(Compose, Read("docker-compose.yml"), "a preview never writes");
    }

    [TestMethod]
    public void TheApplyEndpoint_NeedsTheMode_AndNeverTakesAPathFromTheCaller()
    {
        Savanna(true);
        var r = AdoptionRoutes.HandleComposeApply(new AdoptRequest(_root, "registry"));
        Assert.AreEqual(200, Status(r));
        StringAssert.Contains(Read("docker-compose.yml"), "${FRONTEND_TAG:-");
        Assert.AreEqual(400, Status(AdoptionRoutes.HandleComposeApply(new AdoptRequest(_root, null))));
    }
}
