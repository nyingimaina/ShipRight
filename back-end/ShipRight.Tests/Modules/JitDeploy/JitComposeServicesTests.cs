using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using ShipRight.Modules.JitDeploy;
using ShipRight.Modules.Projects;

namespace ShipRight.Tests.Modules.JitDeploy;

/// <summary>
/// A project with no services of its own can still be built on the server: the services come from the compose file the person
/// picks, which also names the file's place inside the repository.
/// </summary>
[TestClass]
public class JitComposeServicesTests
{
    private string _repo = "";

    [TestInitialize] public void Init() { _repo = Path.Combine(Path.GetTempPath(), "jcs-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_repo); Directory.CreateDirectory(Path.Combine(_repo, ".git")); }
    [TestCleanup] public void Done() { try { Directory.Delete(_repo, true); } catch { /* best effort */ } }

    private string Put(string rel, string content = "")
    {
        var p = Path.Combine(_repo, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
        return p;
    }

    private const string Compose = "services:\n  db:\n    image: mariadb:11.4\n  backend:\n    image: reg.example.com/herd-backend:${IMAGE_TAG:-latest}\n  frontend:\n    image: reg.example.com/herd-frontend:${IMAGE_TAG:-latest}\n  worker:\n    build: ./worker\n    image: ${WORKER_TAG:-shop/worker:latest}\n";

    private string Savanna()
    {
        Put("backend.Dockerfile", "FROM x\nCOPY . .\n");
        Put("frontend.Dockerfile", "FROM x\nCOPY . .\n");
        Put("worker/Dockerfile", "FROM x\n");
        return Put("docker-compose.yml", Compose);
    }

    // ── reading the picked file ──────────────────────────────────────────────

    [TestMethod]
    public void TheFileGivesItsPlaceInTheRepository_AndTheServicesToBuild()
    {
        var file = Savanna();
        var r = ComposeServiceReader.Read(file, "Herd IQ");

        Assert.AreEqual("docker-compose.yml", r.RelativePath);
        CollectionAssert.AreEqual(new[] { "backend", "frontend", "worker" }, r.Services.Select(s => s.Name).ToArray());
        CollectionAssert.AreEqual(new[] { "db" }, r.LeftAlone.ToArray());
        Assert.IsTrue(r.Services.All(s => s.Suggested));
    }

    [TestMethod]
    public void EachServiceGetsItsImageAndItsOwnTagVariable()
    {
        var r = ComposeServiceReader.Read(Savanna(), "Herd IQ");
        var be = r.Services.Single(s => s.Name == "backend");
        Assert.AreEqual("reg.example.com/herd-backend", be.ImageRef);
        Assert.AreEqual("BACKEND_TAG", be.TagVariable);
        Assert.IsNotNull(be.Note, "an image line that does not use its own variable is explained");
        StringAssert.Contains(be.Note!, "BACKEND_TAG");

        var worker = r.Services.Single(s => s.Name == "worker");
        Assert.AreEqual("shop/worker", worker.ImageRef, "the image is read out of its own tag variable");
        Assert.AreEqual("WORKER_TAG", worker.TagVariable);
        Assert.IsNull(worker.Note);
        Assert.IsTrue(worker.Built);
    }

    [TestMethod]
    public void AServiceWithNoImageLine_GetsAnImageNamedFromTheProject()
    {
        var file = Put("docker-compose.yml", "services:\n  api:\n    build: ./api\n");
        var s = ComposeServiceReader.Read(file, "My Shop").Services.Single();
        Assert.AreEqual("my-shop/api", s.ImageRef);
        Assert.AreEqual("API_TAG", s.TagVariable);
    }

    [TestMethod]
    public void AFileInASubfolder_IsNamedRelativeToTheRepository()
    {
        Put("deploy/api/Dockerfile", "FROM x\n");
        var file = Put("deploy/docker-compose.prod.yml", "services:\n  api:\n    build: ./api\n    image: shop/api\n");
        Assert.AreEqual("deploy/docker-compose.prod.yml", ComposeServiceReader.Read(file, "Shop").RelativePath);
    }

    [TestMethod]
    public void NothingToBuild_IsSaidPlainly()
    {
        var file = Put("docker-compose.yml", "services:\n  db:\n    image: mariadb:11\n");
        var r = ComposeServiceReader.Read(file, "Shop");
        Assert.AreEqual(0, r.Services.Count);
        Assert.IsTrue(r.Notes.Any(n => n.Contains("nothing") || n.Contains("no service")));
    }

    [TestMethod]
    public void EveryWayTheFileCannotBeUsed_IsAPlainMessage()
    {
        var notYaml = Put("notes.txt", "x");
        var outside = Path.Combine(Path.GetTempPath(), "jcs-outside-" + Guid.NewGuid().ToString("N") + ".yml");
        File.WriteAllText(outside, "services:\n  a:\n    build: .\n");
        try
        {
            StringAssert.Contains(Assert.ThrowsException<ComposeFileException>(() => ComposeServiceReader.Read(Path.Combine(_repo, "missing.yml"), "S")).Message, "not found");
            StringAssert.Contains(Assert.ThrowsException<ComposeFileException>(() => ComposeServiceReader.Read(notYaml, "S")).Message, ".yml");
            StringAssert.Contains(Assert.ThrowsException<ComposeFileException>(() => ComposeServiceReader.Read(outside, "S")).Message, "git repository");
            var spaced = Put("my folder/docker-compose.yml", "services:\n  a:\n    build: .\n");
            StringAssert.Contains(Assert.ThrowsException<ComposeFileException>(() => ComposeServiceReader.Read(spaced, "S")).Message, "characters");
            var big = Put("big.yml", new string('x', 1_100_000));
            StringAssert.Contains(Assert.ThrowsException<ComposeFileException>(() => ComposeServiceReader.Read(big, "S")).Message, "too large");
        }
        finally { File.Delete(outside); }
    }

    [TestMethod]
    public void ReadingChangesNothing()
    {
        var file = Savanna();
        var before = Directory.GetFiles(_repo, "*", SearchOption.AllDirectories).OrderBy(x => x).ToArray();
        ComposeServiceReader.Read(file, "Herd");
        CollectionAssert.AreEqual(before, Directory.GetFiles(_repo, "*", SearchOption.AllDirectories).OrderBy(x => x).ToArray());
    }

    // ── services kept in the settings ────────────────────────────────────────

    [TestMethod]
    public void TheServicesSurviveSavingAndLoading()
    {
        var cfg = new JitDeployConfig { ComposeServices = [new JitConfiguredService("backend", "reg/herd-backend", "BACKEND_TAG")] };
        var back = JsonConvert.DeserializeObject<JitDeployConfig>(JsonConvert.SerializeObject(cfg))!;
        Assert.AreEqual("backend", back.ComposeServices.Single().Name);
        Assert.AreEqual(0, JsonConvert.DeserializeObject<JitDeployConfig>("{\"Enabled\":true}")!.ComposeServices.Count, "older saved settings have none");
    }

    [TestMethod]
    public void TheSettingsRules_CheckEveryServiceAndRefuseDuplicates()
    {
        JitConfiguredService S(string n = "api", string i = "shop/api", string t = "API_TAG") => new(n, i, t, "Dockerfile");
        int Bad(params JitConfiguredService[] list) => JitDeployConfigValidator.Validate(new JitDeployConfig { Enabled = true, RepoUrl = "git@h:o/r.git", Branch = "main", ComposeServices = list }).Count(p => p.Field == "composeServices");

        Assert.AreEqual(0, Bad(S(), S("web", "shop/web", "WEB_TAG")));
        Assert.AreEqual(1, Bad(S("bad name")));
        Assert.AreEqual(1, Bad(S(i: "Shop/Api")));
        Assert.AreEqual(1, Bad(S(t: "lower")));
        Assert.AreEqual(1, Bad(S(), S(i: "shop/other", t: "OTHER_TAG")), "the same service twice");
    }

    // ── the mapper uses them only when the project has none ──────────────────

    private static ProjectConfig Project(params ServiceConfig[] services) => new()
    {
        Id = "p1", Name = "Herd",
        Services = services.ToList(),
        Server = new ServerConfig { Host = "h", Username = "u", SshKeyPath = "/k", RemoteWorkingDir = "/home/u/herd" },
    };

    private static JitDeployConfig Cfg(params JitConfiguredService[] services) =>
        new() { Enabled = true, RepoUrl = "git@h:o/r.git", Branch = "main", ComposeServices = services };

    [TestMethod]
    public void AProjectWithNoServices_UsesTheOnesFromTheComposeFile()
    {
        var r = JitDeployMapper.Map(Project(), Cfg(new JitConfiguredService("backend", "reg/herd-backend", "BACKEND_TAG")));
        Assert.IsNotNull(r.Request, string.Join(" ", r.Problems));
        var s = r.Request!.Services.Single();
        Assert.AreEqual(("backend", "BACKEND_TAG", "reg/herd-backend"), (s.Name, s.TagVariable, s.ImageRef));
    }

    [TestMethod]
    public void AProjectWithServices_KeepsUsingThem_AsItAlwaysHas()
    {
        var r = JitDeployMapper.Map(Project(new ServiceConfig { Name = "api", DockerImageName = "shop/api" }), Cfg(new JitConfiguredService("backend", "reg/herd-backend", "BACKEND_TAG")));
        Assert.AreEqual("api", r.Request!.Services.Single().Name);
    }

    [TestMethod]
    public void NoServicesAnywhere_StillSaysWhatItAlwaysSaid()
    {
        var r = JitDeployMapper.Map(Project(), Cfg());
        Assert.IsNull(r.Request);
        Assert.IsTrue(r.Problems.Contains("This project has no services to deploy."));
    }

    [TestMethod]
    public void TheCardsServiceList_AlsoComesFromThem()
    {
        var list = JitDeployMapper.Services(Project(), Cfg(new JitConfiguredService("backend", "reg/herd-backend", "BACKEND_TAG")));
        Assert.AreEqual("BACKEND_TAG", list.Single().TagVariable);
        Assert.AreEqual(0, JitDeployMapper.Services(Project()).Count, "the one-argument form is exactly what it was");
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
    private static string Json(IResult r) => System.Text.Json.JsonSerializer.Serialize(((IValueHttpResult)r).Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    [TestMethod]
    public async Task TheEndpoint_ReturnsTheFilePathAndTheServices()
    {
        var store = new MemoryProjects(); await store.SaveAsync(Project());
        var r = await JitComposeFileRoutes.HandleAsync("p1", new ComposeFileRequest(Savanna()), store);

        Assert.AreEqual(200, Status(r));
        var json = Json(r);
        StringAssert.Contains(json, "\"relativePath\":\"docker-compose.yml\"");
        StringAssert.Contains(json, "\"tagVariable\":\"BACKEND_TAG\"");
        StringAssert.Contains(json, "\"leftAlone\":[\"db\"]");
    }

    [TestMethod]
    public async Task TheEndpoint_UnknownProject_AndBadFiles_ArePlain()
    {
        var store = new MemoryProjects(); await store.SaveAsync(Project());
        Assert.AreEqual(404, Status(await JitComposeFileRoutes.HandleAsync("x", new ComposeFileRequest("a.yml"), store)));
        Assert.AreEqual(400, Status(await JitComposeFileRoutes.HandleAsync("p1", new ComposeFileRequest(""), store)));
        var bad = await JitComposeFileRoutes.HandleAsync("p1", new ComposeFileRequest(Path.Combine(_repo, "nope.yml")), store);
        Assert.AreEqual(400, Status(bad));
        StringAssert.Contains(Json(bad), "not found");
    }
}
