using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.JitDeploy;

namespace ShipRight.Tests.Modules.JitDeploy;

/// <summary>The fingerprint report inside a real deploy: it informs, and it can never change or break one.</summary>
[TestClass]
public class JitFingerprintDeployTests
{
    private static readonly JitTarget Target = new("h", "u", "/k", "/home/deploy/shop");

    private static JitDeployRequest Req(bool rebuildAll = false) => new()
    {
        Target = Target, Project = "shop", RepoUrl = "git@github.com:o/r.git", Branch = "master",
        Services = [new JitService("api", "API_TAG", "shop/api"), new JitService("web", "WEB_TAG", "shop/web")],
        RebuildAll = rebuildAll,
    };

    private sealed class Rig
    {
        public FakeRemote Remote { get; } = new() { BuildContextInsideRepo = true };
        public List<string> Lines { get; } = [];
        public JitDeployer Deployer => new(Remote, new FakeClock(), new JitDeployLock());
        public Task<JitDeployResult> Deploy(JitDeployRequest? req = null) =>
            Deployer.DeployAsync(req ?? Req(), l => { lock (Lines) Lines.Add(l); return Task.CompletedTask; });
        public string Report => string.Join("\n", Lines.Where(l => l.StartsWith("[fingerprint]")));
    }

    [TestMethod]
    public async Task The_first_release_says_there_is_nothing_to_compare_with_yet()
    {
        var rig = new Rig();
        var r = await rig.Deploy();
        Assert.AreEqual(JitOutcome.Deployed, r.Outcome, r.Message);
        StringAssert.Contains(rig.Report, "first release");
    }

    [TestMethod]
    public async Task A_second_deploy_with_untouched_build_folders_reports_them_unchanged_and_still_builds_everything()
    {
        var rig = new Rig();
        await rig.Deploy();
        rig.Lines.Clear(); rig.Remote.Events.Clear();
        rig.Remote.Branches["master"] = FakeRemote.Sha2;

        var r = await rig.Deploy();
        Assert.AreEqual(JitOutcome.Deployed, r.Outcome, r.Message);
        StringAssert.Contains(rig.Report, "api: unchanged since r1-aaaaaaa");
        StringAssert.Contains(rig.Report, "web: unchanged since r1-aaaaaaa");
        StringAssert.Contains(rig.Report, "prediction confirmed");
        StringAssert.Contains(rig.Report, "nothing is skipped");
        CollectionAssert.Contains(rig.Remote.Events, "build:api,web", "shadow mode must still build every image");
    }

    [TestMethod]
    public async Task A_changed_build_folder_is_reported_as_changed()
    {
        var rig = new Rig();
        await rig.Deploy();
        rig.Lines.Clear();
        rig.Remote.Branches["master"] = FakeRemote.Sha2;
        rig.Remote.TreeOf = (sha, svc) => svc == "api" && sha == "bbbbbbb" ? "tree-new" : "tree1";

        await rig.Deploy();
        StringAssert.Contains(rig.Report, "api: changed since r1-aaaaaaa");
        StringAssert.Contains(rig.Report, "web: unchanged since r1-aaaaaaa");
    }

    [TestMethod]
    public async Task A_wrong_prediction_is_flagged_when_the_image_differs_despite_an_equal_fingerprint()
    {
        var rig = new Rig();
        await rig.Deploy();
        rig.Lines.Clear();
        rig.Remote.Branches["master"] = FakeRemote.Sha2;
        rig.Remote.ImageIdOf = r => "sha256:" + r;   // every tag is a different image

        await rig.Deploy();
        StringAssert.Contains(rig.Report, "WRONG PREDICTION");
    }

    [TestMethod]
    public async Task The_rebuild_everything_choice_is_acknowledged()
    {
        var rig = new Rig();
        await rig.Deploy();
        rig.Lines.Clear();
        rig.Remote.Branches["master"] = FakeRemote.Sha2;

        await rig.Deploy(Req(rebuildAll: true));
        StringAssert.Contains(rig.Report, "Rebuild everything");
    }

    [TestMethod]
    public async Task A_server_that_cannot_run_the_probe_never_breaks_the_deploy()
    {
        var rig = new Rig();
        await rig.Deploy();
        rig.Lines.Clear();
        rig.Remote.Branches["master"] = FakeRemote.Sha2;
        rig.Remote.ThrowWhen = cmd => cmd.Contains("echo \"svc|");

        var r = await rig.Deploy();
        Assert.AreEqual(JitOutcome.Deployed, r.Outcome, r.Message);
    }

    [TestMethod]
    public async Task Build_folders_outside_the_repository_are_explained_not_guessed()
    {
        var rig = new Rig();
        rig.Remote.BuildContextInsideRepo = false;
        await rig.Deploy();
        StringAssert.Contains(rig.Report, "outside the repository");
    }

    [TestMethod]
    public void The_rebuild_option_defaults_to_off_and_is_read_from_the_request_body()
    {
        Assert.IsFalse(new JitDeployOptions().RebuildAll);
        Assert.IsFalse(new JitDeployRequest { Target = Target, Project = "p", RepoUrl = "u", Services = [] }.RebuildAll);
        var o = System.Text.Json.JsonSerializer.Deserialize<JitDeployOptions>("{\"force\":false,\"rebuildAll\":true}",
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.IsTrue(o!.RebuildAll);
    }
}
