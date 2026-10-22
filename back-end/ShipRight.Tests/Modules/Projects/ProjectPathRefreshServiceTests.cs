using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using ShipRight.Modules.Projects;

namespace ShipRight.Tests.Modules.Projects;

[TestClass]
public class ProjectPathRefreshServiceTests : IDisposable
{
    private readonly string _dataDir;
    private readonly string _root;
    private string _oldContext = "";
    private string _newContext = "";
    private string _versionFile = "";

    public ProjectPathRefreshServiceTests()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), $"sr_refresh_test_{Guid.NewGuid():N}");
        _dataDir = Path.Combine(baseDir, "data");
        _root = Path.Combine(baseDir, "repo");
        _newContext = Path.Combine(_root, "Solution");
        _oldContext = Path.Combine(_root, "Solution", "Project");
        _versionFile = Path.Combine(_oldContext, "version.txt");
        Directory.CreateDirectory(_dataDir);
        Directory.CreateDirectory(_oldContext);
        File.WriteAllText(_versionFile, "1.0.0");
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(Path.GetDirectoryName(_dataDir)!, recursive: true);
    }

    // The real production stack: encrypted SQLite store wrapped by the credential-preserving store.
    private IProjectStore NewStore() =>
        new DockerCredentialPreservingProjectStore(new SqliteProjectStore(_dataDir));

    private ProjectConfig Stored() => new()
    {
        Id = "lattice",
        Name = "Lattice",
        ServerId = "srv-1",
        Services =
        [
            new ServiceConfig
            {
                Name = "Lattice Services Foundation", VersionFilePath = _versionFile, BuildContextPath = _oldContext,
                DockerImageName = "nyingi/lattice-foundation", ComposeServiceName = "lattice",
                DockerUsername = "registry-user", DockerPassword = "registry-s3cret",
                DockerRegistryResourceId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                ImageRetentionCount = 7, LocalImageKeepCount = 3,
            },
        ],
        GitRepos = [new GitConfig { RepoPath = _root, DeployBranch = "master", PushArgs = "--no-verify" }],
        Wsl = new WslConfig { WorkingDir = "/home/x/docker", ComposeRepoUrl = "https://example.test/compose.git" },
        Server = new ServerConfig { Id = "srv-1", Name = "prod", Host = "h", Username = "u", EnvFile = "SECRET=1", ManagedSshKey = true },
        Database = new DatabaseConfig { ContainerName = "db", DatabaseName = "app", RootPassword = "db-s3cret" },
        CreatedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
        ModifiedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
        TimeZone = "Africa/Nairobi",
        WatchBranch = "master",
        WatchSteps = "BuildAndPush",
    };

    private DetectedProjectConfig DetectedNow() => new(
        "repo",
        [new DetectedService("Lattice Services Foundation", _versionFile, _newContext, null, false, null, "1.0.0")],
        [], null, [], []);

    private ProjectPathRefreshService NewService(IProjectStore store, Action<string>? onDetect = null) =>
        new(store, root => { onDetect?.Invoke(root); return DetectedNow(); });

    private static string Json(object o) => JsonConvert.SerializeObject(o);

    [TestMethod]
    public async Task Preview_ReturnsTheChanges_AndWritesNothing()
    {
        var store = NewStore();
        await store.SaveAsync(Stored());
        var before = Json((await store.GetByIdAsync("lattice"))!);

        var outcome = await NewService(store).PreviewAsync("lattice", _root);

        Assert.AreEqual(1, outcome!.Plan.Changes.Count);
        Assert.AreEqual(_newContext, outcome.Plan.Changes[0].NewBuildContextPath);
        Assert.IsNull(outcome.Project);
        Assert.AreEqual(before, Json((await NewStore().GetByIdAsync("lattice"))!));
    }

    [TestMethod]
    public async Task Apply_ChangesOnlyTheBuildContext_AndEverySecretSurvivesAReloadFromDisk()
    {
        await NewStore().SaveAsync(Stored());

        var outcome = await NewService(NewStore()).ApplyAsync("lattice", _root);

        Assert.AreEqual(_newContext, outcome!.Project!.Services[0].BuildContextPath);

        // A brand new store reads straight from the encrypted file on disk.
        var reloaded = (await NewStore().GetByIdAsync("lattice"))!;
        Assert.AreEqual(_newContext, reloaded.Services[0].BuildContextPath);
        Assert.AreEqual("registry-user", reloaded.Services[0].DockerUsername);
        Assert.AreEqual("registry-s3cret", reloaded.Services[0].DockerPassword);
        Assert.AreEqual("db-s3cret", reloaded.Database!.RootPassword);
        Assert.AreEqual("SECRET=1", reloaded.Server.EnvFile);
        Assert.AreEqual("https://example.test/compose.git", reloaded.Wsl.ComposeRepoUrl);

        var expected = Stored();
        expected = expected with
        {
            Services = [expected.Services[0] with { BuildContextPath = _newContext }],
            ModifiedAt = reloaded.ModifiedAt,
        };
        Assert.AreEqual(Json(expected), Json(reloaded));
    }

    [TestMethod]
    public async Task Apply_WithNothingToChange_DoesNotSaveAnything()
    {
        var already = Stored() with { Services = [Stored().Services[0] with { BuildContextPath = _newContext }] };
        await NewStore().SaveAsync(already);
        var before = Json((await NewStore().GetByIdAsync("lattice"))!);

        var outcome = await NewService(NewStore()).ApplyAsync("lattice", _root);

        Assert.AreEqual(0, outcome!.Plan.Changes.Count);
        Assert.AreEqual(before, Json((await NewStore().GetByIdAsync("lattice"))!));
    }

    [TestMethod]
    public async Task UnknownProject_ReturnsNull()
    {
        Assert.IsNull(await NewService(NewStore()).PreviewAsync("nope", _root));
        Assert.IsNull(await NewService(NewStore()).ApplyAsync("nope", _root));
    }

    [TestMethod]
    public async Task BlankRootPath_UsesTheFirstGitRepoOfTheProject()
    {
        await NewStore().SaveAsync(Stored());
        string? detectedAt = null;

        await NewService(NewStore(), r => detectedAt = r).PreviewAsync("lattice", "  ");

        Assert.AreEqual(_root, detectedAt);
    }

    [TestMethod]
    public async Task BlankRootPath_WithNoGitRepo_IsRefused()
    {
        await NewStore().SaveAsync(Stored() with { GitRepos = [] });

        await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => NewService(NewStore()).PreviewAsync("lattice", ""));
    }

    [TestMethod]
    public async Task ARootPathThatDoesNotExistOrClimbsUp_IsRefused_BeforeAnyDetection()
    {
        await NewStore().SaveAsync(Stored());
        var detections = 0;
        var service = NewService(NewStore(), _ => detections++);

        await Assert.ThrowsExceptionAsync<ArgumentException>(() => service.PreviewAsync("lattice", Path.Combine(_root, "missing")));
        await Assert.ThrowsExceptionAsync<ArgumentException>(() => service.ApplyAsync("lattice", _root + "/../"));
        Assert.AreEqual(0, detections);
    }
}
