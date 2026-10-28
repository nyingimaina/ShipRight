using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Identity;
using ShipRight.Modules.Setup;

namespace ShipRight.Tests.Modules.Setup;

[TestClass]
public class SetupRoutesTests
{
    private const string WebCsproj =
        "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>";

    private static (int Status, JsonElement Body) Run(IResult result)
    {
        var ctx = new DefaultHttpContext();
        ctx.RequestServices = new ServiceCollection().AddLogging().AddProblemDetails().BuildServiceProvider();
        ctx.Response.Body = new MemoryStream();
        result.ExecuteAsync(ctx).GetAwaiter().GetResult();
        ctx.Response.Body.Position = 0;
        var text = new StreamReader(ctx.Response.Body).ReadToEnd();
        return (ctx.Response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    // ── Plan ─────────────────────────────────────────────────────────────────

    [TestMethod]
    public void Plan_ReturnsComponentsItemsAndWarnings()
    {
        using var t = new TempTree();
        t.Add("Api/Api.csproj", WebCsproj);

        var (status, body) = Run(SetupRoutes.HandlePlan(new SetupPlanRequest(t.Root)));

        Assert.AreEqual(200, status);
        Assert.AreEqual(1, body.GetProperty("plan").GetProperty("components").GetArrayLength());
        Assert.IsTrue(body.GetProperty("plan").GetProperty("items").GetArrayLength() >= 6);
    }

    [TestMethod]
    public void Plan_IsReadOnly()
    {
        using var t = new TempTree();
        t.Add("Api/Api.csproj", WebCsproj);
        var before = Directory.GetFileSystemEntries(t.Root, "*", SearchOption.AllDirectories).Length;

        SetupRoutes.HandlePlan(new SetupPlanRequest(t.Root));

        Assert.AreEqual(before, Directory.GetFileSystemEntries(t.Root, "*", SearchOption.AllDirectories).Length);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    public void Plan_BlankRoot_IsABadRequest(string? root)
    {
        var (status, body) = Run(SetupRoutes.HandlePlan(new SetupPlanRequest(root)));

        Assert.AreEqual(400, status);
        Assert.AreEqual("rootPath", body.GetProperty("field").GetString());
    }

    [TestMethod]
    public void Plan_MissingFolder_IsABadRequest()
    {
        var (status, _) = Run(SetupRoutes.HandlePlan(
            new SetupPlanRequest(Path.Combine(Path.GetTempPath(), "sr_nope_" + Guid.NewGuid().ToString("N")))));

        Assert.AreEqual(400, status);
    }

    // ── Apply ────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Apply_WritesOnlyTheApprovedItems_RecomputingThePlanOnTheServer()
    {
        using var t = new TempTree();
        t.Add("Api/Api.csproj", WebCsproj);
        var dockerfileId = SetupPlanner.CreateDefault().Plan(t.Root).Items.Single(i => i.Kind == PlanItemKind.Dockerfile).Id;

        var (status, body) = Run(await SetupRoutes.HandleApplyAsync(
            new SetupApplyRequest(t.Root, [dockerfileId], false), new RecordingRunner(), default));

        Assert.AreEqual(200, status);
        Assert.IsTrue(File.Exists(t.Full("Api/Dockerfile")));
        Assert.IsFalse(File.Exists(t.Full(".gitignore")));
        Assert.IsFalse(body.GetProperty("result").GetProperty("committed").GetBoolean());
    }

    [TestMethod]
    public async Task Apply_IgnoresIdsThatAreNotInTheServersPlan_SoClientsCannotInjectFiles()
    {
        using var t = new TempTree();
        t.Add("Api/Api.csproj", WebCsproj);

        var (status, _) = Run(await SetupRoutes.HandleApplyAsync(
            new SetupApplyRequest(t.Root, ["VersionFile:../../evil.txt", "Dockerfile:Other/Dockerfile"], false),
            new RecordingRunner(), default));

        Assert.AreEqual(200, status);
        Assert.IsFalse(File.Exists(t.Full("Other/Dockerfile")));
        Assert.IsFalse(File.Exists(Path.Combine(t.Root, "..", "..", "evil.txt")));
    }

    [TestMethod]
    public async Task Apply_BlankRoot_IsABadRequest()
    {
        var (status, _) = Run(await SetupRoutes.HandleApplyAsync(new SetupApplyRequest("", [], false), new RecordingRunner(), default));

        Assert.AreEqual(400, status);
    }

    // ── Tree status ──────────────────────────────────────────────────────────

    [TestMethod]
    public async Task TreeStatus_ReportsCleanStateAndCommit()
    {
        using var t = new TempTree();
        var sha = new string('a', 40);
        var runner = new RecordingRunner().When((_, a) => a.Contains("rev-parse"), stdout: sha);

        var (status, body) = Run(await SetupRoutes.HandleTreeStatusAsync(new TreeStatusRequest(t.Root), runner, default));

        Assert.AreEqual(200, status);
        Assert.IsTrue(body.GetProperty("isClean").GetBoolean());
        Assert.AreEqual(sha, body.GetProperty("sha").GetString());
    }

    [TestMethod]
    public async Task TreeStatus_MissingFolder_IsABadRequestAndRunsNoGit()
    {
        var runner = new RecordingRunner();

        var (status, _) = Run(await SetupRoutes.HandleTreeStatusAsync(
            new TreeStatusRequest(Path.Combine(Path.GetTempPath(), "sr_nope_" + Guid.NewGuid().ToString("N"))), runner, default));

        Assert.AreEqual(400, status);
        Assert.AreEqual(0, runner.Calls.Count);
    }

    // ── Commit ───────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Commit_StagesTheChosenPathsOnly()
    {
        using var t = new TempTree();
        var runner = new RecordingRunner();

        var (status, body) = Run(await SetupRoutes.HandleCommitAsync(
            new CommitRequest(t.Root, "Save my work", ["a.cs"]), runner, default));

        Assert.AreEqual(200, status);
        Assert.IsTrue(body.GetProperty("success").GetBoolean());
        Assert.IsTrue(runner.Any("add -- a.cs"));
    }

    [TestMethod]
    public async Task Commit_RefusedFiles_AreABadRequestWithTheReason()
    {
        using var t = new TempTree();

        var (status, body) = Run(await SetupRoutes.HandleCommitAsync(
            new CommitRequest(t.Root, "msg", [".env"]), new RecordingRunner(), default));

        Assert.AreEqual(400, status);
        StringAssert.Contains(body.GetProperty("message").GetString()!, ".env");
    }

    // ── WSL home ─────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task WslHome_ReturnsTheHomeFolderInsideWsl()
    {
        var runner = new RecordingRunner().When((_, a) => a.Contains("printenv"), stdout: "/home/dev\n");

        var (status, body) = Run(await SetupRoutes.HandleWslHomeAsync(runner, default));

        Assert.AreEqual(200, status);
        Assert.AreEqual("/home/dev", body.GetProperty("home").GetString());
    }

    [TestMethod]
    public async Task WslHome_WhenWslIsMissing_IsABadRequestWithAnExplanation()
    {
        var runner = new RecordingRunner().When((_, a) => a.Contains("printenv"), exit: 1, stderr: "no distro");

        var (status, body) = Run(await SetupRoutes.HandleWslHomeAsync(runner, default));

        Assert.AreEqual(400, status);
        StringAssert.Contains(body.GetProperty("message").GetString()!, "WSL");
    }

    // ── Access ───────────────────────────────────────────────────────────────

    [TestMethod]
    [DataRow("POST", "/api/setup/plan")]
    [DataRow("POST", "/api/setup/apply")]
    [DataRow("POST", "/api/setup/tree-status")]
    [DataRow("POST", "/api/setup/commit")]
    [DataRow("GET", "/api/setup/wsl-home")]
    public void SetupEndpoints_NeedAnAdmin_BecauseTheyReadAndWriteTheHostsFolders(string method, string path)
    {
        Assert.AreEqual(AppRoles.Admin, RoleAccessRules.RequiredRole(method, path));
    }
}
