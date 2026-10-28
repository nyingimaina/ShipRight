using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using ShipRight.Modules.Projects;

namespace ShipRight.Tests.Modules.Projects;

[TestClass]
public class BuildSourceTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly JsonProjectStore _inner;
    private readonly BuildSourcePreservingProjectStore _wrapper;

    public BuildSourceTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"sr_bs_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmpDir);
        _inner = new JsonProjectStore(_tmpDir);
        _wrapper = new BuildSourcePreservingProjectStore(_inner);
    }

    public void Dispose() => Directory.Delete(_tmpDir, recursive: true);

    private static ProjectConfig Project(BuildSource? source = null) => new()
    {
        Id = "bs1",
        Name = "Build Source",
        Services = [new ServiceConfig { Name = "api", VersionFilePath = "/tmp/v.txt", BuildContextPath = "/tmp", DockerImageName = "test/api" }],
        GitRepos = [new GitConfig { RepoPath = "/tmp", DeployBranch = "main" }],
        BuildSource = source,
        CreatedAt = DateTime.UtcNow,
        ModifiedAt = DateTime.UtcNow,
    };

    // ── The setting ──────────────────────────────────────────────────────────

    [TestMethod]
    public void ProjectsThatNeverChoseASource_BuildInPlaceAsTheyAlwaysHave()
    {
        Assert.IsNull(Project().BuildSource);
        Assert.IsFalse(Project().UsesCleanSnapshot());
        Assert.IsFalse(Project(BuildSource.InPlace).UsesCleanSnapshot());
        Assert.IsTrue(Project(BuildSource.CleanSnapshot).UsesCleanSnapshot());
    }

    [TestMethod]
    public void ProjectsSavedBeforeTheSettingExisted_LoadWithoutIt()
    {
        var legacy = "{\"Id\":\"old\",\"Name\":\"Old\",\"Version\":2,\"Services\":[],\"GitRepos\":[]}";

        var loaded = JsonConvert.DeserializeObject<ProjectConfig>(legacy)!;

        Assert.IsNull(loaded.BuildSource);
        Assert.IsFalse(loaded.UsesCleanSnapshot());
    }

    [TestMethod]
    public void TheSetting_SurvivesBeingSavedAndLoaded()
    {
        var json = JsonConvert.SerializeObject(Project(BuildSource.CleanSnapshot));

        Assert.AreEqual(BuildSource.CleanSnapshot, JsonConvert.DeserializeObject<ProjectConfig>(json)!.BuildSource);
    }

    // ── Editing must not silently switch it off ─────────────────────────────

    [TestMethod]
    public async Task SavingWithoutASetting_KeepsTheOneAlreadyChosen()
    {
        await _inner.SaveAsync(Project(BuildSource.CleanSnapshot));

        // What the original wizard sends: it knows nothing about the setting.
        await _wrapper.SaveAsync(Project(source: null));

        Assert.AreEqual(BuildSource.CleanSnapshot, (await _inner.GetByIdAsync("bs1"))!.BuildSource);
    }

    [TestMethod]
    public async Task SavingWithAnExplicitSetting_ChangesIt()
    {
        await _inner.SaveAsync(Project(BuildSource.CleanSnapshot));

        await _wrapper.SaveAsync(Project(BuildSource.InPlace));

        Assert.AreEqual(BuildSource.InPlace, (await _inner.GetByIdAsync("bs1"))!.BuildSource);
    }

    [TestMethod]
    public async Task ANewProject_IsSavedAsSent()
    {
        await _wrapper.SaveAsync(Project(BuildSource.CleanSnapshot));
        Assert.AreEqual(BuildSource.CleanSnapshot, (await _inner.GetByIdAsync("bs1"))!.BuildSource);
    }

    [TestMethod]
    public async Task ANewProjectWithNoSetting_StaysUnset()
    {
        await _wrapper.SaveAsync(Project(source: null));
        Assert.IsNull((await _inner.GetByIdAsync("bs1"))!.BuildSource);
    }

    [TestMethod]
    public async Task EverythingElseIsPassedThroughToTheInnerStore()
    {
        await _wrapper.SaveAsync(Project());

        Assert.AreEqual(1, _wrapper.Count);
        Assert.AreEqual("Build Source", (await _wrapper.GetByNameAsync("Build Source"))!.Name);
        Assert.AreEqual(1, (await _wrapper.GetAllAsync()).Count);

        await _wrapper.DeleteAsync("bs1");
        Assert.IsNull(await _wrapper.GetByIdAsync("bs1"));
    }
}
