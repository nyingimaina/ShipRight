using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Identity;
using ShipRight.Modules.Projects;

namespace ShipRight.Tests.Modules.Projects;

[TestClass]
public class ProjectBuildSourceRoutesTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly JsonProjectStore _store;

    public ProjectBuildSourceRoutesTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"sr_bsr_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmpDir);
        _store = new JsonProjectStore(_tmpDir);
    }

    public void Dispose() => Directory.Delete(_tmpDir, recursive: true);

    private static (int Status, JsonElement Body) Run(IResult result)
    {
        var ctx = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().AddProblemDetails().BuildServiceProvider() };
        ctx.Response.Body = new MemoryStream();
        result.ExecuteAsync(ctx).GetAwaiter().GetResult();
        ctx.Response.Body.Position = 0;
        var text = new StreamReader(ctx.Response.Body).ReadToEnd();
        return (ctx.Response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private async Task Seed(BuildSource? source = null) =>
        await _store.SaveAsync(new ProjectConfig { Id = "p1", Name = "Shop", BuildSource = source, ModifiedAt = new DateTime(2020, 1, 1) });

    [TestMethod]
    public async Task OptingIn_IsSavedAndReported()
    {
        await Seed();

        var (status, body) = Run(await ProjectBuildSourceRoutes.HandleSetAsync("p1", new BuildSourceRequest(BuildSource.CleanSnapshot), _store));

        Assert.AreEqual(200, status);
        Assert.AreEqual("CleanSnapshot", body.GetProperty("buildSource").GetString());
        Assert.AreEqual(BuildSource.CleanSnapshot, (await _store.GetByIdAsync("p1"))!.BuildSource);
    }

    [TestMethod]
    public async Task SwitchingBack_IsSaved()
    {
        await Seed(BuildSource.CleanSnapshot);

        Run(await ProjectBuildSourceRoutes.HandleSetAsync("p1", new BuildSourceRequest(BuildSource.InPlace), _store));

        Assert.AreEqual(BuildSource.InPlace, (await _store.GetByIdAsync("p1"))!.BuildSource);
    }

    [TestMethod]
    public async Task NothingElseAboutTheProjectChanges_ExceptWhenItWasModified()
    {
        await Seed();
        var before = (await _store.GetByIdAsync("p1"))!;

        Run(await ProjectBuildSourceRoutes.HandleSetAsync("p1", new BuildSourceRequest(BuildSource.CleanSnapshot), _store));

        var after = (await _store.GetByIdAsync("p1"))!;
        string Json(ProjectConfig p) => Newtonsoft.Json.JsonConvert.SerializeObject(p with { BuildSource = null, ModifiedAt = default });
        Assert.AreEqual(Json(before), Json(after));
        Assert.IsTrue(after.ModifiedAt > before.ModifiedAt);
    }

    [TestMethod]
    public async Task UnknownProject_IsNotFound()
    {
        var (status, _) = Run(await ProjectBuildSourceRoutes.HandleSetAsync("nope", new BuildSourceRequest(BuildSource.CleanSnapshot), _store));

        Assert.AreEqual(404, status);
    }

    [TestMethod]
    public async Task MissingValue_IsABadRequest_AndChangesNothing()
    {
        await Seed(BuildSource.CleanSnapshot);

        var (status, body) = Run(await ProjectBuildSourceRoutes.HandleSetAsync("p1", new BuildSourceRequest(null), _store));

        Assert.AreEqual(400, status);
        Assert.AreEqual("buildSource", body.GetProperty("field").GetString());
        Assert.AreEqual(BuildSource.CleanSnapshot, (await _store.GetByIdAsync("p1"))!.BuildSource);
    }

    [TestMethod]
    public void ChangingHowAProjectBuilds_NeedsAnAdmin_LikeEveryOtherProjectSetting()
    {
        Assert.AreEqual(AppRoles.Admin, RoleAccessRules.RequiredRole("POST", "/api/projects/p1/build-source"));
    }
}
