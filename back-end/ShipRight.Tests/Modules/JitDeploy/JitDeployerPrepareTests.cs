using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.JitDeploy;

namespace ShipRight.Tests.Modules.JitDeploy;

/// <summary>
/// A deploy prepares the server by itself when git, Docker or Docker Compose is missing: it reports progress, stops
/// with a plain message if a step fails (changing nothing else), and carries on with the deploy when it succeeds.
/// </summary>
[TestClass]
public class JitDeployerPrepareTests
{
    private static readonly JitTarget Target = new("h", "u", "/k", "/home/deploy/shop");

    private static JitDeployRequest Req() => new()
    {
        Target = Target,
        Project = "shop",
        RepoUrl = "git@github.com:o/r.git",
        Branch = "master",
        Services = [new JitService("api", "API_TAG", "shop/api"), new JitService("web", "WEB_TAG", "shop/web")],
    };

    private sealed class Rig
    {
        public FakeRemote Remote { get; } = new();
        public JitDeployLock Lock { get; } = new();
        public List<string> Lines { get; } = [];
        public string All => string.Join("\n", Lines);

        public Task<JitDeployResult> Deploy(CancellationToken ct = default) =>
            new JitDeployer(Remote, new FakeClock(), Lock).DeployAsync(Req(), l => { lock (Lines) Lines.Add(l); return Task.CompletedTask; }, ct);
    }

    private static string[] Prep(Rig r) => r.Remote.Events.Where(e => e.StartsWith("prepare")).ToArray();

    [TestMethod]
    public async Task EverythingPresent_OnlyChecks_SaysSo_AndDeploys()
    {
        var rig = new Rig();
        var r = await rig.Deploy();

        Assert.AreEqual(JitOutcome.Deployed, r.Outcome, r.Message);
        CollectionAssert.AreEqual(new[] { "prepare-detect" }, Prep(rig));
        StringAssert.Contains(rig.All, "[prepare]");
        StringAssert.Contains(rig.All, "already");
    }

    [TestMethod]
    public async Task MissingGitAndDocker_AreInstalledInOrder_ThenTheDeployCarriesOn()
    {
        var rig = new Rig();
        rig.Remote.MissingTools.Add(ServerTool.Git);
        rig.Remote.MissingTools.Add(ServerTool.Docker);
        var r = await rig.Deploy();

        Assert.AreEqual(JitOutcome.Deployed, r.Outcome, r.Message);
        CollectionAssert.AreEqual(
            new[] { "prepare-detect", "prepare:git", "prepare:docker", "prepare:daemon", "prepare:compose", "prepare-detect" },
            Prep(rig));
        var events = rig.Remote.Events;
        Assert.IsTrue(events.LastIndexOf("prepare-detect") < events.IndexOf("docker-check"), "preparation finishes before the normal checks");
    }

    [TestMethod]
    public async Task Progress_NamesEachStep_WithItsNumber_AndReportsSuccess()
    {
        var rig = new Rig();
        rig.Remote.MissingTools.Add(ServerTool.Git);
        rig.Remote.MissingTools.Add(ServerTool.Compose);
        await rig.Deploy();

        StringAssert.Contains(rig.All, "missing");
        StringAssert.Contains(rig.All, "Step 1 of 2");
        StringAssert.Contains(rig.All, "Step 2 of 2");
        StringAssert.Contains(rig.All, "The server is ready");
        StringAssert.Contains(rig.All, "installed Git","the output of the step itself is forwarded");
    }

    [TestMethod]
    public async Task OnlyTheMissingTool_IsInstalled()
    {
        var rig = new Rig();
        rig.Remote.MissingTools.Add(ServerTool.Compose);
        var r = await rig.Deploy();

        Assert.AreEqual(JitOutcome.Deployed, r.Outcome, r.Message);
        CollectionAssert.AreEqual(new[] { "prepare-detect", "prepare:compose", "prepare-detect" }, Prep(rig));
    }

    [TestMethod]
    public async Task AFailingStep_StopsTheDeploy_NamesTheStep_AndChangesNothingElse()
    {
        var rig = new Rig();
        rig.Remote.MissingTools.Add(ServerTool.Git);
        rig.Remote.MissingTools.Add(ServerTool.Docker);
        rig.Remote.InstallFails = t => t == ServerTool.Docker;
        var r = await rig.Deploy();

        Assert.AreEqual(JitOutcome.FailedBeforeChange, r.Outcome);
        StringAssert.Contains(r.Message, "prepare the server");
        StringAssert.Contains(r.Message, "Docker");
        StringAssert.Contains(r.Message, "Unable to locate package", "the cause is shown, not hidden");
        StringAssert.Contains(rig.All, "FAILED");
        CollectionAssert.DoesNotContain(rig.Remote.Events, "prepare:daemon", "later steps do not run after a failure");
        CollectionAssert.DoesNotContain(rig.Remote.Events, "docker-check");
        Assert.IsNull(rig.Remote.StateJson, "not even a state file is created");
        Assert.IsFalse(rig.Remote.Events.Any(e => e.StartsWith("build:") || e.StartsWith("stop:") || e.StartsWith("start:")));
    }

    [TestMethod]
    public async Task AStepThatClaimsSuccessButChangedNothing_IsCaughtByTheRecheck()
    {
        var rig = new Rig();
        rig.Remote.MissingTools.Add(ServerTool.Git);
        rig.Remote.InstallLiesAboutSuccess = true;
        var r = await rig.Deploy();

        Assert.AreEqual(JitOutcome.FailedBeforeChange, r.Outcome);
        StringAssert.Contains(r.Message, "still missing");
        StringAssert.Contains(r.Message, "git");
        Assert.IsNull(rig.Remote.StateJson);
    }

    [TestMethod]
    public async Task ADroppedConnection_DuringPreparation_IsReportedAsLostContact_AndTheLockIsReleased()
    {
        var rig = new Rig();
        rig.Remote.MissingTools.Add(ServerTool.Git);
        rig.Remote.ThrowWhen = c => c.Contains("prepare:git");
        var r = await rig.Deploy();

        Assert.AreEqual(JitOutcome.ConnectionLost, r.Outcome);
        using var again = rig.Lock.TryAcquire(JitDeployLock.KeyFor(Target));
        Assert.IsNotNull(again);
    }

    [TestMethod]
    public async Task RunningItAgain_AfterPreparation_OnlyChecks()
    {
        var rig = new Rig();
        rig.Remote.MissingTools.Add(ServerTool.Git);
        await rig.Deploy();
        rig.Remote.Events.Clear();
        rig.Remote.Branches["master"] = FakeRemote.Sha2;
        var r = await rig.Deploy();

        Assert.AreEqual(JitOutcome.Deployed, r.Outcome, r.Message);
        CollectionAssert.AreEqual(new[] { "prepare-detect" }, Prep(rig), "idempotent: nothing is installed twice");
    }

    [TestMethod]
    public async Task APreparedServer_WithNoDockerAccess_StillStopsWithTheNormalDockerMessage()
    {
        var rig = new Rig();
        rig.Remote.DockerOk = false;
        var r = await rig.Deploy();

        Assert.AreEqual(JitOutcome.FailedBeforeChange, r.Outcome);
        StringAssert.Contains(r.Message, "Docker is not available");
    }
}
