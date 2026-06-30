using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Projects;

namespace ShipRight.Tests.Modules.Projects;

[TestClass]
public class ProjectRouterTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly JsonProjectStore _store;

    public ProjectRouterTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"sr_router_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmpDir);
        _store = new JsonProjectStore(_tmpDir);
    }

    public void Dispose() => Directory.Delete(_tmpDir, recursive: true);

    private static ProjectConfig ValidProject(string id = "test-proj", string name = "Test Project") => new()
    {
        Id = id,
        Name = name,
        Services =
        [
            new ServiceConfig
            {
                Name = "api",
                VersionFilePath = "/tmp/version.txt",
                BuildContextPath = "/tmp",
                DockerImageName = "nyingi/test-api"
            }
        ],
        GitRepos = [new GitConfig { RepoPath = "/tmp", DeployBranch = "main" }],
        Wsl = new WslConfig { WorkingDir = "/home/ubuntu/app" },
        Server = new ServerConfig
        {
            Host = "1.2.3.4",
            Username = "ubuntu",
            SshKeyPath = "/tmp/key.pem",
            RemoteWorkingDir = "/home/ubuntu/app",
            RebuildScript = "rebuild.sh"
        },
        CreatedAt = DateTime.UtcNow,
        ModifiedAt = DateTime.UtcNow,
    };

    private static T FieldValue<T>(object error, string propertyName)
    {
        var prop = error.GetType().GetProperty(propertyName)
            ?? throw new InvalidOperationException($"Property '{propertyName}' not found on error object.");
        return (T)prop.GetValue(error)!;
    }

    // ── Happy path ──────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Validate_ValidProject_ReturnsNoErrors()
    {
        var errors = await ProjectRouter.ValidateAsync(ValidProject(), _store, isNew: true);
        Assert.AreEqual(0, errors.Count);
    }

    // ── Name ────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Validate_EmptyName_ReturnsNameError()
    {
        var p = ValidProject() with { Name = "" };
        var errors = await ProjectRouter.ValidateAsync(p, _store, isNew: true);

        var nameErr = errors.Single();
        Assert.AreEqual("name", FieldValue<string>(nameErr, "field"));
    }

    [TestMethod]
    public async Task Validate_DuplicateName_ReturnsNameError()
    {
        await _store.SaveAsync(ValidProject("existing-id", "My App"));
        var p = ValidProject("new-id", "My App");
        var errors = await ProjectRouter.ValidateAsync(p, _store, isNew: true);

        Assert.IsTrue(errors.Any(e => FieldValue<string>(e, "field") == "name"));
    }

    [TestMethod]
    public async Task Validate_SameNameSameId_NoError()
    {
        await _store.SaveAsync(ValidProject("same-id", "My App"));
        var p = ValidProject("same-id", "My App");
        var errors = await ProjectRouter.ValidateAsync(p, _store, isNew: false);

        Assert.IsFalse(errors.Any(e => FieldValue<string>(e, "field") == "name"));
    }

    // ── Services ────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Validate_ServiceMissingDockerImageName_ReturnsFieldError()
    {
        var svc = new ServiceConfig
        {
            Name = "api",
            VersionFilePath = "/tmp/version.txt",
            BuildContextPath = "/tmp",
            DockerImageName = ""
        };
        var p = ValidProject() with { Services = [svc] };
        var errors = await ProjectRouter.ValidateAsync(p, _store, isNew: true);

        Assert.IsTrue(errors.Any(e => FieldValue<string>(e, "field") == "services[0].dockerImageName"));
    }

    [TestMethod]
    public async Task Validate_ServiceDockerImageWithTag_ReturnsFieldError()
    {
        var svc = new ServiceConfig
        {
            Name = "api",
            VersionFilePath = "/tmp/version.txt",
            BuildContextPath = "/tmp",
            DockerImageName = "nyingi/api:latest"
        };
        var p = ValidProject() with { Services = [svc] };
        var errors = await ProjectRouter.ValidateAsync(p, _store, isNew: true);

        var err = errors.Single();
        Assert.AreEqual("services[0].dockerImageName", FieldValue<string>(err, "field"));
        StringAssert.Contains(FieldValue<string>(err, "message"), "tag");
    }

    // ── GitRepos ─────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Validate_NoGitRepos_ReturnsGitReposError()
    {
        var p = ValidProject() with { GitRepos = [] };
        var errors = await ProjectRouter.ValidateAsync(p, _store, isNew: true);

        Assert.IsTrue(errors.Any(e => FieldValue<string>(e, "field") == "gitRepos"));
    }

    [TestMethod]
    public async Task Validate_GitRepoMissingRepoPath_ReturnsFieldError()
    {
        var p = ValidProject() with { GitRepos = [new GitConfig { RepoPath = "", DeployBranch = "main" }] };
        var errors = await ProjectRouter.ValidateAsync(p, _store, isNew: true);

        Assert.IsTrue(errors.Any(e => FieldValue<string>(e, "field") == "gitRepos[0].repoPath"));
    }

    [TestMethod]
    public async Task Validate_GitRepoDeployBranchTooLong_ReturnsFieldError()
    {
        var longBranch = new string('x', 101);
        var p = ValidProject() with { GitRepos = [new GitConfig { RepoPath = "/tmp", DeployBranch = longBranch }] };
        var errors = await ProjectRouter.ValidateAsync(p, _store, isNew: true);

        Assert.IsTrue(errors.Any(e => FieldValue<string>(e, "field") == "gitRepos[0].deployBranch"));
    }

    [TestMethod]
    public async Task Validate_MultipleGitRepos_AllValidated()
    {
        var p = ValidProject() with
        {
            GitRepos =
            [
                new GitConfig { RepoPath = "/tmp/repo1", DeployBranch = "main" },
                new GitConfig { RepoPath = "",            DeployBranch = "" },
            ]
        };
        var errors = await ProjectRouter.ValidateAsync(p, _store, isNew: true);

        Assert.IsTrue(errors.Any(e => FieldValue<string>(e, "field") == "gitRepos[1].repoPath"));
        Assert.IsTrue(errors.Any(e => FieldValue<string>(e, "field") == "gitRepos[1].deployBranch"));
    }

    // ── WSL ─────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Validate_WslDirEmpty_ReturnsWslError()
    {
        var p = ValidProject() with { Wsl = new WslConfig { WorkingDir = "" } };
        var errors = await ProjectRouter.ValidateAsync(p, _store, isNew: true);

        Assert.IsTrue(errors.Any(e => FieldValue<string>(e, "field") == "wsl.workingDir"));
    }

    [TestMethod]
    public async Task Validate_WslDirNotAbsoluteLinuxPath_ReturnsWslError()
    {
        var p = ValidProject() with { Wsl = new WslConfig { WorkingDir = "home/ubuntu" } };
        var errors = await ProjectRouter.ValidateAsync(p, _store, isNew: true);

        var err = errors.Single();
        Assert.AreEqual("wsl.workingDir", FieldValue<string>(err, "field"));
        StringAssert.Contains(FieldValue<string>(err, "message"), "/");
    }

    // ── Server ───────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Validate_ServerHostMissing_ReturnsServerHostError()
    {
        var p = ValidProject() with
        {
            Server = new ServerConfig
            {
                Host = "",
                Username = "ubuntu",
                SshKeyPath = "/tmp/key.pem",
                RemoteWorkingDir = "/home/ubuntu",
                RebuildScript = "rebuild.sh"
            }
        };
        var errors = await ProjectRouter.ValidateAsync(p, _store, isNew: true);

        Assert.IsTrue(errors.Any(e => FieldValue<string>(e, "field") == "server.host"));
    }

    [TestMethod]
    public async Task Validate_RemoteWorkingDirNotAbsolute_ReturnsServerError()
    {
        var p = ValidProject() with
        {
            Server = new ServerConfig
            {
                Host = "1.2.3.4",
                Username = "ubuntu",
                SshKeyPath = "/tmp/key.pem",
                RemoteWorkingDir = "home/ubuntu",
                RebuildScript = "rebuild.sh"
            }
        };
        var errors = await ProjectRouter.ValidateAsync(p, _store, isNew: true);

        Assert.IsTrue(errors.Any(e => FieldValue<string>(e, "field") == "server.remoteWorkingDir"));
    }

    [TestMethod]
    public async Task Validate_RebuildScriptWithPathSeparator_ReturnsServerError()
    {
        var p = ValidProject() with
        {
            Server = new ServerConfig
            {
                Host = "1.2.3.4",
                Username = "ubuntu",
                SshKeyPath = "/tmp/key.pem",
                RemoteWorkingDir = "/home/ubuntu",
                RebuildScript = "scripts/rebuild.sh"
            }
        };
        var errors = await ProjectRouter.ValidateAsync(p, _store, isNew: true);

        Assert.IsTrue(errors.Any(e => FieldValue<string>(e, "field") == "server.rebuildScript"));
    }

    // ── ID generation ────────────────────────────────────────────────────────

    [TestMethod]
    public async Task GenerateId_SlugifiesName()
    {
        var id = await ProjectRouter.GenerateUniqueIdAsync("My Cool App", _store);
        Assert.AreEqual("my-cool-app", id);
    }

    [TestMethod]
    public async Task GenerateId_CollisionGetsNumericSuffix()
    {
        await _store.SaveAsync(ValidProject("my-app", "My App"));
        var id = await ProjectRouter.GenerateUniqueIdAsync("My App", _store);
        Assert.AreEqual("my-app-2", id);
    }
}
