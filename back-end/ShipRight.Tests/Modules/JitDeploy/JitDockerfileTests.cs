using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.DockerAdoption;
using ShipRight.Modules.JitDeploy;
using ShipRight.Modules.Projects;

namespace ShipRight.Tests.Modules.JitDeploy;

/// <summary>Each service's Dockerfile is found for the person, and can be changed by picking another file.</summary>
[TestClass]
public class JitDockerfileTests
{
    private string _repo = "";

    [TestInitialize] public void Init() { _repo = Path.Combine(Path.GetTempPath(), "jdf-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path.Combine(_repo, ".git")); }
    [TestCleanup] public void Done() { try { Directory.Delete(_repo, true); } catch { /* best effort */ } }

    private string Put(string rel, string content = "")
    {
        var p = Path.Combine(_repo, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
        return p;
    }

    private string Savanna()
    {
        Put("backend.Dockerfile", "FROM x AS build\nCOPY back-end/app.csproj back-end/\nFROM y\nCOPY --from=build /a /a\n");
        Put("frontend.Dockerfile", "FROM x\nARG NEXT_PUBLIC_API_URL\nCOPY front-end/package.json ./\n");
        Put("back-end/app.csproj", "<Project/>");
        Put("front-end/package.json", "{}");
        return Put("docker-compose.yml", "services:\n  db:\n    image: mariadb:11\n  backend:\n    image: reg/herd-backend:${IMAGE_TAG:-latest}\n  frontend:\n    image: reg/herd-frontend:${IMAGE_TAG:-latest}\n");
    }

    // ── found with the compose file ──────────────────────────────────────────

    [TestMethod]
    public void EachServiceComesWithItsDockerfileAndBuildFolder()
    {
        var r = ComposeServiceReader.Read(Savanna(), "Herd");
        var be = r.Services.Single(s => s.Name == "backend");
        Assert.AreEqual("backend.Dockerfile", be.Dockerfile);
        Assert.AreEqual(".", be.Context, "the repository root: it copies back-end/... from there");
        Assert.IsNull(be.Target);
        Assert.AreEqual(0, be.ArgsNeedingAValue.Count);

        var fe = r.Services.Single(s => s.Name == "frontend");
        Assert.AreEqual("frontend.Dockerfile", fe.Dockerfile);
        CollectionAssert.AreEqual(new[] { "NEXT_PUBLIC_API_URL" }, fe.ArgsNeedingAValue.ToArray());
    }

    [TestMethod]
    public void ABuildSectionInTheComposeFile_GivesTheRecipe()
    {
        Put("svc/Dockerfile.prod", "FROM x AS build\nFROM x AS runner\n");
        var file = Put("docker-compose.yml", "services:\n  api:\n    image: shop/api\n    build:\n      context: ./svc\n      dockerfile: Dockerfile.prod\n      target: runner\n      args:\n        MODE: prod\n");
        var s = ComposeServiceReader.Read(file, "Shop").Services.Single();
        Assert.AreEqual(("svc/Dockerfile.prod", "svc", "runner"), (s.Dockerfile, s.Context, s.Target), "the Dockerfile is a path inside the repository");
        Assert.AreEqual("prod", s.Args!["MODE"]);
    }

    [TestMethod]
    public void AServiceNoDockerfileFits_HasNoneYet_SoThePersonMustPickOne()
    {
        var file = Put("docker-compose.yml", "services:\n  api:\n    build: ./api\n");
        var s = ComposeServiceReader.Read(file, "Shop").Services.Single();
        Assert.AreEqual("api/Dockerfile", s.Dockerfile, "a build section with no file name means the file called Dockerfile, in its build folder");
        Assert.AreEqual("api", s.Context);
    }

    // ── describing a Dockerfile that was picked ──────────────────────────────

    [TestMethod]
    public void APickedDockerfile_IsNamedInTheRepository_WithItsBuildFolderStagesAndArguments()
    {
        Savanna();
        Put("ops/api.Dockerfile", "FROM x AS base\nFROM base AS api\nFROM base AS worker\nARG API_URL\nCOPY services/api/package.json services/api/\n");
        Put("services/api/package.json", "{}");
        var d = DockerfileDescriber.Describe(Path.Combine(_repo, "ops", "api.Dockerfile"));

        Assert.AreEqual(_repo, d.RepoRoot);
        Assert.AreEqual("ops/api.Dockerfile", d.DockerfileRelative);
        Assert.AreEqual(".", d.ContextRelative);
        Assert.AreEqual("ops/api.Dockerfile", d.DockerfileInContext);
        CollectionAssert.AreEqual(new[] { "api" }, d.OtherStages.ToArray());
        CollectionAssert.AreEqual(new[] { "API_URL" }, d.ArgsNeedingAValue.ToArray());
    }

    [TestMethod]
    public void ADockerfileThatCopiesFromItsOwnFolder_BuildsFromThere()
    {
        Put("api/package.json", "{}");
        var d = DockerfileDescriber.Describe(Put("api/Dockerfile", "FROM x\nCOPY package.json ./\n"));
        Assert.AreEqual("api", d.ContextRelative);
        Assert.AreEqual("Dockerfile", d.DockerfileInContext);
    }

    [TestMethod]
    public void ADockerfileOutsideAnyRepository_IsAPlainError()
    {
        var outside = Path.Combine(Path.GetTempPath(), "jdf-out-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        var file = Path.Combine(outside, "Dockerfile");
        File.WriteAllText(file, "FROM x\n");
        try
        {
            StringAssert.Contains(Assert.ThrowsException<ComposeFileException>(() => DockerfileDescriber.Describe(file)).Message, "git repository");
            StringAssert.Contains(Assert.ThrowsException<ComposeFileException>(() => DockerfileDescriber.Describe(Path.Combine(outside, "nope"))).Message, "not found");
        }
        finally { Directory.Delete(outside, true); }
    }

    [TestMethod]
    public void APathWithSpaces_IsRefusedWithAReason()
    {
        var spaced = Put("my folder/Dockerfile", "FROM x\n");
        StringAssert.Contains(Assert.ThrowsException<ComposeFileException>(() => DockerfileDescriber.Describe(spaced)).Message, "characters");
    }

    // ── the endpoint ─────────────────────────────────────────────────────────

    private sealed class MemoryProjects : IProjectStore
    {
        public Dictionary<string, ProjectConfig> All { get; } = [];
        public int Count => All.Count;
        public Task<ProjectConfig?> GetByNameAsync(string name) => Task.FromResult(All.Values.FirstOrDefault(p => p.Name == name));
        public Task<List<ProjectConfig>> GetAllAsync() => Task.FromResult(All.Values.ToList());
        public Task<ProjectConfig?> GetByIdAsync(string id) => Task.FromResult(All.GetValueOrDefault(id));
        public Task SaveAsync(ProjectConfig p) { All[p.Id] = p; return Task.CompletedTask; }
        public Task DeleteAsync(string id) { All.Remove(id); return Task.CompletedTask; }
    }

    private static int Status(IResult r) => ((IStatusCodeHttpResult)r).StatusCode ?? 200;
    private static string Json(IResult r) => JsonSerializer.Serialize(((IValueHttpResult)r).Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    [TestMethod]
    public async Task TheEndpoint_DescribesThePickedDockerfile()
    {
        Savanna();
        var store = new MemoryProjects(); await store.SaveAsync(new ProjectConfig { Id = "p1", Name = "Herd" });
        var r = await JitDockerfileRoutes.HandleAsync("p1", new DockerfileRequest(Path.Combine(_repo, "backend.Dockerfile")), store);

        Assert.AreEqual(200, Status(r));
        var json = Json(r);
        StringAssert.Contains(json, "\"dockerfileRelative\":\"backend.Dockerfile\"");
        StringAssert.Contains(json, "\"contextRelative\":\".\"");
    }

    [TestMethod]
    public async Task TheEndpoint_UnknownProject_BlankPath_AndBadFile_ArePlain()
    {
        var store = new MemoryProjects(); await store.SaveAsync(new ProjectConfig { Id = "p1", Name = "Herd" });
        Assert.AreEqual(404, Status(await JitDockerfileRoutes.HandleAsync("x", new DockerfileRequest("a"), store)));
        Assert.AreEqual(400, Status(await JitDockerfileRoutes.HandleAsync("p1", new DockerfileRequest(""), store)));
        var bad = await JitDockerfileRoutes.HandleAsync("p1", new DockerfileRequest(Path.Combine(_repo, "missing")), store);
        Assert.AreEqual(400, Status(bad));
        StringAssert.Contains(Json(bad), "not found");
    }
}
