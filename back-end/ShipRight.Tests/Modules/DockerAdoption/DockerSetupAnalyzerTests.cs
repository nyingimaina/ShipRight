using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.DockerAdoption;
using ShipRight.Modules.Setup;

namespace ShipRight.Tests.Modules.DockerAdoption;

/// <summary>Reads an existing Docker setup (Dockerfiles with any name, a compose file) and works out the build recipe of each service.</summary>
[TestClass]
public class DockerSetupAnalyzerTests
{
    private string _root = "";

    [TestInitialize] public void Init() { _root = Path.Combine(Path.GetTempPath(), "adopt-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_root); }
    [TestCleanup] public void Done() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    private void File_(string rel, string content = "")
    {
        var p = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
    }

    private const string BackendDockerfile = "FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build\nWORKDIR /src\nCOPY [\"back-end/company-man.csproj\", \"back-end/\"]\nRUN dotnet restore\nCOPY . .\nFROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final\nCOPY --from=build /app /app\n";
    private const string FrontendDockerfile = "FROM node:20-alpine AS deps\nCOPY front-end/package.json front-end/package-lock.json* ./\nCOPY front-end/scripts/ ./scripts/\nFROM node:20-alpine AS runner\nCOPY --from=deps /app /app\n";

    private const string SavannaCompose = """
        services:
          nginx-proxy:
            image: nginxproxy/nginx-proxy
          acme-companion:
            image: nginxproxy/acme-companion
            depends_on:
              - nginx-proxy
          db:
            image: mariadb:11.4
          minio:
            image: minio/minio:latest
          backend:
            image: 825347768469.dkr.ecr.eu-north-1.amazonaws.com/savanna-herdiq-backend:${IMAGE_TAG:-latest}
            depends_on:
              db:
                condition: service_healthy
          frontend:
            image: 825347768469.dkr.ecr.eu-north-1.amazonaws.com/savanna-herdiq-frontend:${IMAGE_TAG:-latest}
            depends_on:
              - backend
        volumes:
          db-data:
        """;

    private void Savanna()
    {
        File_("docker-compose.yml", SavannaCompose);
        File_("backend.Dockerfile", BackendDockerfile);
        File_("frontend.Dockerfile", FrontendDockerfile);
        File_("back-end/dockerfile", "FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build-env\nCOPY . ./\n");
        File_("back-end/company-man.csproj", "<Project/>");
        File_("front-end/package.json", "{}");
        File_("front-end/scripts/x.js", "");
        File_("deploy-starter-pack/docker-compose.yml", SavannaCompose);
        File_("deploy-starter-pack/backend.Dockerfile", BackendDockerfile);
        File_("deploy-starter-pack/frontend.Dockerfile", FrontendDockerfile);
        File_("node_modules/pkg/Dockerfile", "FROM x\n");
        File_("front-end/node_modules/pkg/Dockerfile", "FROM x\n");
    }

    private AdoptionPlan Plan() => DockerSetupAnalyzer.Analyse(_root);

    // ── a real layout: custom names, root context, a stray Dockerfile, infrastructure services ──

    [TestMethod]
    public void SavannaLayout_BackendAndFrontendAreFound_WithRootContextAndTheirOwnDockerfile()
    {
        Savanna();
        var plan = Plan();

        Assert.AreEqual("docker-compose.yml", plan.ComposeFile);
        CollectionAssert.AreEqual(new[] { "backend", "frontend" }, plan.Services.Select(s => s.Name).ToArray());

        var be = plan.Services[0];
        Assert.AreEqual("backend.Dockerfile", be.DockerfilePath, "relative to the build context");
        Assert.AreEqual(".", be.ContextRelative);
        Assert.AreEqual("backend", be.ComposeService);
        Assert.AreEqual("825347768469.dkr.ecr.eu-north-1.amazonaws.com/savanna-herdiq-backend", be.ImageName);
        Assert.AreEqual(Confidence.High, be.Confidence);
        Assert.IsNull(be.Target, "the last stage is Docker's own default");

        var fe = plan.Services[1];
        Assert.AreEqual("frontend.Dockerfile", fe.DockerfilePath);
        Assert.AreEqual(".", fe.ContextRelative);
        Assert.AreEqual("825347768469.dkr.ecr.eu-north-1.amazonaws.com/savanna-herdiq-frontend", fe.ImageName);
    }

    [TestMethod]
    public void SavannaLayout_TheInfrastructureServicesAreLeftAlone()
    {
        Savanna();
        CollectionAssert.AreEquivalent(new[] { "nginx-proxy", "acme-companion", "db", "minio" }, Plan().ThirdParty.ToArray());
    }

    [TestMethod]
    public void SavannaLayout_TheStrayAndNestedCopies_AreNotChosen_ButOfferedAsAlternatives()
    {
        Savanna();
        var plan = Plan();
        var be = plan.Services[0];
        CollectionAssert.Contains(be.Alternatives.ToArray(), "back-end/dockerfile");
        CollectionAssert.Contains(be.Alternatives.ToArray(), "deploy-starter-pack/backend.Dockerfile");
        CollectionAssert.Contains(plan.UnusedDockerfiles.ToArray(), "back-end/dockerfile");
        Assert.IsFalse(plan.UnusedDockerfiles.Any(u => u.Contains("node_modules")), "vendored folders are never searched");
    }

    [TestMethod]
    public void SavannaLayout_TheSharedTagVariableIsReported()
    {
        Savanna();
        var plan = Plan();
        Assert.AreEqual(TagStyle.SharedVariable, plan.Services[0].TagStyle);
        Assert.AreEqual("IMAGE_TAG", plan.Services[0].TagVariable);
        Assert.IsTrue(plan.Warnings.Any(w => w.Contains("IMAGE_TAG") && w.Contains("independent")), string.Join("\n", plan.Warnings));
    }

    [TestMethod]
    public void ANestedSecondComposeFile_IsMentioned_NotUsed()
    {
        Savanna();
        var plan = Plan();
        Assert.AreEqual("docker-compose.yml", plan.ComposeFile);
        Assert.IsTrue(plan.Warnings.Any(w => w.Contains("deploy-starter-pack/docker-compose.yml")));
    }

    // ── other shapes ─────────────────────────────────────────────────────────

    [TestMethod]
    public void NoCompose_EachDockerfileBecomesAServiceNamedFromItsFolder()
    {
        File_("api/Dockerfile", "FROM x\nCOPY package.json ./\n");
        File_("api/package.json", "{}");
        File_("web/Dockerfile", "FROM x\nCOPY . .\n");
        var plan = Plan();

        Assert.IsNull(plan.ComposeFile);
        CollectionAssert.AreEqual(new[] { "api", "web" }, plan.Services.Select(s => s.Name).ToArray());
        Assert.AreEqual("api", plan.Services[0].ContextRelative);
        Assert.AreEqual("Dockerfile", plan.Services[0].DockerfilePath);
        Assert.AreEqual(Confidence.Medium, plan.Services[0].Confidence);
        Assert.IsNull(plan.Services[0].ComposeService);
    }

    [TestMethod]
    public void SeveralDockerfilesInOneFolder_EachBecomeTheirOwnService()
    {
        File_("Dockerfile.api", "FROM x\nCOPY . .\n");
        File_("Dockerfile.web", "FROM x\nCOPY . .\n");
        File_("worker.Dockerfile", "FROM x\nCOPY . .\n");
        var plan = Plan();

        CollectionAssert.AreEqual(new[] { "api", "web", "worker" }, plan.Services.Select(s => s.Name).ToArray());
        Assert.IsTrue(plan.Services.All(s => s.ContextRelative == "."));
        CollectionAssert.AreEqual(new[] { "Dockerfile.api", "Dockerfile.web", "worker.Dockerfile" }, plan.Services.Select(s => s.DockerfilePath).ToArray());
    }

    [TestMethod]
    public void AComposeBuildSection_IsTheSourceOfTruth()
    {
        File_("docker-compose.yml", """
            services:
              api:
                image: shop/api
                build:
                  context: ./svc
                  dockerfile: Dockerfile.prod
                  target: runner
                  args:
                    NODE_ENV: production
            """);
        File_("svc/Dockerfile.prod", "FROM x AS build\nFROM x AS runner\n");
        File_("svc/Dockerfile", "FROM x\n");
        var s = Plan().Services.Single();

        Assert.AreEqual("api", s.Name);
        Assert.AreEqual("svc", s.ContextRelative);
        Assert.AreEqual("Dockerfile.prod", s.DockerfilePath);
        Assert.AreEqual("runner", s.Target);
        Assert.AreEqual("production", s.BuildArgs["NODE_ENV"]);
        Assert.AreEqual("shop/api", s.ImageName);
        Assert.AreEqual(Confidence.High, s.Confidence);
    }

    [TestMethod]
    public void TheContextIsFoundFromWhatTheDockerfileCopies()
    {
        File_("ops/api.Dockerfile", "FROM x\nCOPY services/api/package.json services/api/\n");
        File_("services/api/package.json", "{}");
        var s = Plan().Services.Single();

        Assert.AreEqual(".", s.ContextRelative, "the nearest folder where everything it copies exists");
        Assert.AreEqual("ops/api.Dockerfile", s.DockerfilePath);
    }

    [TestMethod]
    public void AnImageOnlyComposeServiceThatTwoDockerfilesEquallyFit_IsNotGuessed()
    {
        File_("docker-compose.yml", "services:\n  worker:\n    image: shop/worker:1\n");
        File_("a/worker/Dockerfile", "FROM x\n");
        File_("b/worker/Dockerfile", "FROM x\n");
        var plan = Plan();
        var s = plan.Services.Single(x => x.ComposeService == "worker");

        Assert.AreEqual(Confidence.Low, s.Confidence);
        Assert.IsNull(s.DockerfilePath);
        CollectionAssert.AreEquivalent(new[] { "a/worker/Dockerfile", "b/worker/Dockerfile" }, s.Alternatives.ToArray());
        Assert.IsTrue(s.Reasons.Any(r => r.Contains("several")));
    }

    [TestMethod]
    public void ARecognisedUnrelatedImage_IsThirdParty_WhenNoDockerfileFitsIt()
    {
        File_("docker-compose.yml", "services:\n  cache:\n    image: redis:7\n  api:\n    image: shop/api\n");
        File_("api/Dockerfile", "FROM x\n");
        var plan = Plan();

        CollectionAssert.AreEqual(new[] { "cache" }, plan.ThirdParty.ToArray());
        Assert.AreEqual("api", plan.Services.Single().ComposeService);
    }

    [TestMethod]
    public void ExtraEndStagesAreOfferedAsOptionalImages()
    {
        File_("Dockerfile", "FROM node AS base\nFROM base AS api\nFROM base AS worker\n");
        var s = Plan().Services.Single();
        CollectionAssert.AreEqual(new[] { "api" }, s.OtherStages.ToArray());
    }

    [TestMethod]
    public void ArgumentsWithNoValue_AreFlagged()
    {
        File_("Dockerfile", "FROM node\nARG API_URL\nARG MODE=prod\n");
        var s = Plan().Services.Single();
        CollectionAssert.AreEqual(new[] { "API_URL" }, s.ArgsNeedingAValue.ToArray());
    }

    [TestMethod]
    public void NothingToAdopt_SaysSoPlainly()
    {
        var plan = Plan();
        Assert.AreEqual(0, plan.Services.Count);
        Assert.IsTrue(plan.Warnings.Any(w => w.Contains("No Dockerfile")));
    }

    [TestMethod]
    public void AMissingFolder_IsAClearError() =>
        Assert.ThrowsException<DirectoryNotFoundException>(() => DockerSetupAnalyzer.Analyse(Path.Combine(_root, "nope")));

    [TestMethod]
    public void NothingIsWrittenOrChanged()
    {
        Savanna();
        var before = Directory.GetFiles(_root, "*", SearchOption.AllDirectories).OrderBy(x => x).ToArray();
        Plan();
        CollectionAssert.AreEqual(before, Directory.GetFiles(_root, "*", SearchOption.AllDirectories).OrderBy(x => x).ToArray());
    }
}
