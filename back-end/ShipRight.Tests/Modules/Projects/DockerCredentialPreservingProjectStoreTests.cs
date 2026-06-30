using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Projects;

namespace ShipRight.Tests.Modules.Projects;

[TestClass]
public class DockerCredentialPreservingProjectStoreTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly JsonProjectStore _inner;
    private readonly DockerCredentialPreservingProjectStore _wrapper;

    public DockerCredentialPreservingProjectStoreTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"sr_dc_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmpDir);
        _inner = new JsonProjectStore(_tmpDir);
        _wrapper = new DockerCredentialPreservingProjectStore(_inner);
    }

    public void Dispose() => Directory.Delete(_tmpDir, recursive: true);

    private static ProjectConfig MakeProject(string id, string name) => new()
    {
        Id = id,
        Name = name,
        Services = [new ServiceConfig { Name = "api", VersionFilePath = "/tmp/v.txt", BuildContextPath = "/tmp", DockerImageName = "test/api" }],
        GitRepos = [new GitConfig { RepoPath = "/tmp", DeployBranch = "main" }],
        Wsl = new WslConfig { WorkingDir = "/home/test" },
        Server = new ServerConfig { Host = "1.2.3.4", Username = "ubuntu", SshKeyPath = "/tmp/k.pem", RemoteWorkingDir = "/home/ubuntu", RebuildScript = "rebuild.sh" },
        CreatedAt = DateTime.UtcNow,
        ModifiedAt = DateTime.UtcNow,
    };

    [TestMethod]
    public async Task Save_PreservesDockerPasswordWhenEmptyOnUpdate()
    {
        var p = MakeProject("dc-test", "DC Test");
        p = p with
        {
            Services = p.Services.Select(s => s with
            {
                DockerUsername = "registry-user",
                DockerPassword = "s3cret!",
            }).ToList()
        };
        await _inner.SaveAsync(p);

        var updated = p with
        {
            Services = p.Services.Select(s => s with
            {
                DockerUsername = "",
                DockerPassword = "",
            }).ToList()
        };
        await _wrapper.SaveAsync(updated);

        var reloaded = await _inner.GetByIdAsync("dc-test");
        Assert.IsNotNull(reloaded);
        Assert.AreEqual("registry-user", reloaded!.Services[0].DockerUsername);
        Assert.AreEqual("s3cret!", reloaded.Services[0].DockerPassword);
    }

    [TestMethod]
    public async Task Save_DoesNotAffectNewProjects()
    {
        var p = MakeProject("dc-new", "DC New");
        p = p with
        {
            Services = p.Services.Select(s => s with
            {
                DockerUsername = "new-user",
                DockerPassword = "new-pass",
            }).ToList()
        };
        await _wrapper.SaveAsync(p);

        var reloaded = await _inner.GetByIdAsync("dc-new");
        Assert.IsNotNull(reloaded);
        Assert.AreEqual("new-user", reloaded!.Services[0].DockerUsername);
        Assert.AreEqual("new-pass", reloaded.Services[0].DockerPassword);
    }

    [TestMethod]
    public async Task Save_AllowsExplicitCredentialChange()
    {
        var p = MakeProject("dc-change", "DC Change");
        p = p with
        {
            Services = p.Services.Select(s => s with
            {
                DockerUsername = "old-user",
                DockerPassword = "old-pass",
            }).ToList()
        };
        await _inner.SaveAsync(p);

        var updated = p with
        {
            Services = p.Services.Select(s => s with
            {
                DockerUsername = "new-user",
                DockerPassword = "new-pass",
            }).ToList()
        };
        await _wrapper.SaveAsync(updated);

        var reloaded = await _inner.GetByIdAsync("dc-change");
        Assert.IsNotNull(reloaded);
        Assert.AreEqual("new-user", reloaded!.Services[0].DockerUsername);
        Assert.AreEqual("new-pass", reloaded.Services[0].DockerPassword);
    }

    [TestMethod]
    public async Task Save_MatchesServicesByName_NotByIndex()
    {
        var p = MakeProject("dc-name", "DC Name");
        p = p with
        {
            Services =
            [
                new ServiceConfig
                {
                    Name = "api", DockerUsername = "api-user", DockerPassword = "api-pass",
                    VersionFilePath = "/tmp/api/v.txt", BuildContextPath = "/tmp/api", DockerImageName = "test/api"
                },
                new ServiceConfig
                {
                    Name = "web", DockerUsername = "web-user", DockerPassword = "web-pass",
                    VersionFilePath = "/tmp/web/v.txt", BuildContextPath = "/tmp/web", DockerImageName = "test/web"
                },
            ]
        };
        await _inner.SaveAsync(p);

        // Simulate form submission where "web" service order changes before "api"
        var reordered = p with
        {
            Services =
            [
                p.Services[1] with { DockerUsername = "", DockerPassword = "" },
                p.Services[0] with { DockerUsername = "", DockerPassword = "" },
            ]
        };
        await _wrapper.SaveAsync(reordered);

        var reloaded = await _inner.GetByIdAsync("dc-name");
        Assert.IsNotNull(reloaded);
        // Both should have preserved their original credentials (matched by name, not index)
        var api = reloaded.Services.First(s => s.Name == "api");
        Assert.AreEqual("api-user", api.DockerUsername, "api should keep its user");
        Assert.AreEqual("api-pass", api.DockerPassword, "api should keep its pass");
        var web = reloaded.Services.First(s => s.Name == "web");
        Assert.AreEqual("web-user", web.DockerUsername, "web should keep its user");
        Assert.AreEqual("web-pass", web.DockerPassword, "web should keep its pass");
    }

    [TestMethod]
    public async Task DelegatesToInnerStore()
    {
        var p = MakeProject("dc-delegate", "DC Delegate");
        await _wrapper.SaveAsync(p);

        var all = await _wrapper.GetAllAsync();
        Assert.AreEqual(1, all.Count);

        var byId = await _wrapper.GetByIdAsync("dc-delegate");
        Assert.IsNotNull(byId);

        var byName = await _wrapper.GetByNameAsync("DC Delegate");
        Assert.IsNotNull(byName);

        await _wrapper.DeleteAsync("dc-delegate");
        Assert.AreEqual(0, await _inner.GetAllAsync().ContinueWith(t => t.Result.Count));
    }
}
