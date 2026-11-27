using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.JitDeploy;
using ShipRight.Modules.Projects;
using ShipRight.Tests.Modules.Setup;

namespace ShipRight.Tests.Modules.JitDeploy;

/// <summary>The download link must serve only that project's own attempt, and only while it is retained.</summary>
[TestClass]
public class JitDeployAttemptRoutesTests
{
    private sealed class Projects : IProjectStore
    {
        public Dictionary<string, ProjectConfig> Items { get; } = [];
        public int Count => Items.Count;
        public Task<List<ProjectConfig>> GetAllAsync() => Task.FromResult(Items.Values.ToList());
        public Task<ProjectConfig?> GetByIdAsync(string id) => Task.FromResult(Items.GetValueOrDefault(id));
        public Task<ProjectConfig?> GetByNameAsync(string name) => Task.FromResult(Items.Values.FirstOrDefault(p => p.Name == name));
        public Task SaveAsync(ProjectConfig p) { Items[p.Id] = p; return Task.CompletedTask; }
        public Task DeleteAsync(string id) { Items.Remove(id); return Task.CompletedTask; }
    }

    private static int Status(IResult r) => ((IStatusCodeHttpResult)r).StatusCode ?? 200;

    [TestMethod]
    public async Task UnknownProject_Is404()
    {
        using var tree = new TempTree();
        var r = await JitDeployRoutes.HandleDownloadAttemptAsync("nope", "a1", new Projects(), new JitDeployAttemptStore(tree.Root));
        Assert.AreEqual(404, Status(r));
    }

    [TestMethod]
    public async Task UnknownAttempt_Is404()
    {
        using var tree = new TempTree();
        var projects = new Projects();
        projects.Items["p1"] = new ProjectConfig { Id = "p1", Name = "Shop" };

        var r = await JitDeployRoutes.HandleDownloadAttemptAsync("p1", "missing", projects, new JitDeployAttemptStore(tree.Root));

        Assert.AreEqual(404, Status(r));
    }

    [TestMethod]
    public async Task AnotherProjectsAttempt_Is404()
    {
        using var tree = new TempTree();
        var store = new JitDeployAttemptStore(tree.Root);
        var id = JitDeployAttemptStore.NewId(DateTime.UtcNow);
        await store.WriteAsync("p1", id, "secret");
        var projects = new Projects();
        projects.Items["p2"] = new ProjectConfig { Id = "p2", Name = "Other" };

        var r = await JitDeployRoutes.HandleDownloadAttemptAsync("p2", id, projects, store);

        Assert.AreEqual(404, Status(r));
    }

    [TestMethod]
    public async Task KnownAttempt_IsServedAsADownload()
    {
        using var tree = new TempTree();
        var store = new JitDeployAttemptStore(tree.Root);
        var id = JitDeployAttemptStore.NewId(DateTime.UtcNow);
        await store.WriteAsync("p1", id, "diagnostics body");
        var projects = new Projects();
        projects.Items["p1"] = new ProjectConfig { Id = "p1", Name = "Shop" };

        var r = await JitDeployRoutes.HandleDownloadAttemptAsync("p1", id, projects, store);

        var file = (FileContentHttpResult)r;
        CollectionAssert.AreEqual(System.Text.Encoding.UTF8.GetBytes("diagnostics body"), file.FileContents.ToArray());
        Assert.AreEqual($"shipright-deploy-{id}.log", file.FileDownloadName);
        StringAssert.StartsWith(file.ContentType!, "text/plain");
    }

    [TestMethod]
    public async Task UnsafeAttemptId_Is404NeverAFilePath()
    {
        using var tree = new TempTree();
        var store = new JitDeployAttemptStore(tree.Root);
        var projects = new Projects();
        projects.Items["p1"] = new ProjectConfig { Id = "p1", Name = "Shop" };

        var r = await JitDeployRoutes.HandleDownloadAttemptAsync("p1", "..\\..\\secret", projects, store);

        Assert.AreEqual(404, Status(r));
    }
}
