using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.JitDeploy;

namespace ShipRight.Tests.Modules.JitDeploy;

/// <summary>"Restart all services": compose down, then up again from the release that is already running. Not a new release.</summary>
[TestClass]
public class JitRestartAllTests
{
    private static readonly JitTarget Target = new("h", "u", "/k", "/home/deploy/shop");

    private static JitDeployRequest Req(string? env = null) => new()
    {
        Target = Target, Project = "shop", RepoUrl = "git@github.com:o/r.git", Branch = "master",
        Services = [new JitService("api", "API_TAG", "shop/api"), new JitService("web", "WEB_TAG", "shop/web")],
        EnvContent = env,
    };

    private sealed class Rig
    {
        public FakeRemote Remote { get; } = new();
        public JitDeployLock Lock { get; } = new();
        public List<string> Lines { get; } = [];
        private JitDeployer Deployer => new(Remote, new FakeClock(), Lock);
        private Task Log(string l) { lock (Lines) Lines.Add(l); return Task.CompletedTask; }

        public Task<JitDeployResult> Deploy() => Deployer.DeployAsync(Req(), Log);
        public Task<JitDeployResult> Restart(JitDeployRequest? req = null) => Deployer.RestartAllAsync(req ?? Req(), Log);

        public async Task SeedAsync()
        {
            var r = await Deploy();
            Assert.AreEqual(JitOutcome.Deployed, r.Outcome, r.Message);
            Remote.Events.Clear(); Remote.Commands.Clear(); Remote.Writes.Clear(); Lines.Clear();
        }
    }

    [TestMethod]
    public async Task Brings_everything_down_then_up_again_on_the_release_that_is_running()
    {
        var rig = new Rig();
        await rig.SeedAsync();
        var before = rig.Remote.ReadState();

        var r = await rig.Restart();
        Assert.AreEqual(JitOutcome.Restarted, r.Outcome, r.Message);
        Assert.IsTrue(r.Success);
        Assert.AreEqual("r1-aaaaaaa", r.Tag);

        var e = rig.Remote.Events;
        var iDown = e.IndexOf("down");
        var iUp = e.IndexOf("up-all");
        Assert.IsTrue(iDown >= 0 && iUp > iDown, string.Join(" | ", e));
        Assert.IsFalse(e.Any(x => x.StartsWith("build:")), "a restart never builds");
        Assert.AreEqual("shop/api:r1-aaaaaaa", rig.Remote.Running["api"]);
        Assert.AreEqual("shop/web:r1-aaaaaaa", rig.Remote.Running["web"]);
        Assert.IsTrue(rig.Remote.Running.ContainsKey("db"), "services that ShipRight does not build come back too");

        var after = rig.Remote.ReadState();
        Assert.AreEqual(before.CurrentTag, after.CurrentTag);
        Assert.AreEqual(before.Releases.Count, after.Releases.Count);
        Assert.AreEqual(DeployPhase.Idle, after.Phase);
    }

    [TestMethod]
    public async Task Starts_from_the_commit_of_the_running_release_and_refreshes_the_variables_first()
    {
        var rig = new Rig();
        await rig.SeedAsync();
        rig.Remote.Branches["master"] = FakeRemote.Sha2;   // the branch has moved on, but the running release has not

        await rig.Restart(Req("MARIADB_USER=app"));

        var e = rig.Remote.Events;
        CollectionAssert.Contains(e, "checkout:aaaaaaa");
        Assert.AreEqual("MARIADB_USER=app", rig.Remote.EnvContent!.Trim());
        Assert.IsTrue(e.IndexOf("env-written") < e.IndexOf("down"), "changed variables are written before anything is stopped");
    }

    [TestMethod]
    public async Task Checks_every_service_is_healthy_afterwards_in_dependency_order()
    {
        var rig = new Rig();
        await rig.SeedAsync();
        await rig.Restart();
        var probes = rig.Remote.Events.Where(x => x.StartsWith("probe:")).Select(x => x[6..]).Distinct().ToList();
        CollectionAssert.AreEqual(new[] { "db", "api", "web" }, probes);
    }

    [TestMethod]
    public async Task Never_removes_volumes_and_never_builds_or_pulls()
    {
        var rig = new Rig();
        await rig.SeedAsync();
        await rig.Restart();
        var down = rig.Remote.Commands.Single(c => c.Contains(" down "));
        var up = rig.Remote.Commands.Single(c => c.Contains("up -d --no-build"));
        Assert.IsFalse(down.Contains(" -v") || down.Contains("--volumes") || down.Contains("--rmi"), down);
        StringAssert.Contains(down, "docker compose -p 'shop'");
        StringAssert.Contains(down, "API_TAG='shop/api:r1-aaaaaaa'");
        StringAssert.Contains(up, "--pull never");
    }

    [TestMethod]
    public async Task Refuses_when_nothing_has_been_deployed_yet_and_touches_nothing()
    {
        var rig = new Rig();
        var r = await rig.Restart();
        Assert.AreEqual(JitOutcome.FailedBeforeChange, r.Outcome);
        StringAssert.Contains(r.Message, "no release");
        CollectionAssert.DoesNotContain(rig.Remote.Events, "down");
    }

    [TestMethod]
    public async Task Refuses_while_the_server_state_needs_attention()
    {
        var rig = new Rig();
        await rig.SeedAsync();
        rig.Remote.StateJson = ReleaseStateJson.Serialize(rig.Remote.ReadState() with { Phase = DeployPhase.RollbackFailed });
        var r = await rig.Restart();
        Assert.AreEqual(JitOutcome.Blocked, r.Outcome);
        CollectionAssert.DoesNotContain(rig.Remote.Events, "down");
    }

    [TestMethod]
    public async Task Refuses_when_the_running_releases_images_are_gone_so_nothing_is_stopped_for_nothing()
    {
        var rig = new Rig();
        await rig.SeedAsync();
        rig.Remote.Images.Clear();
        var r = await rig.Restart();
        Assert.AreEqual(JitOutcome.FailedBeforeChange, r.Outcome);
        CollectionAssert.DoesNotContain(rig.Remote.Events, "down");
    }

    [TestMethod]
    public async Task A_service_that_does_not_come_back_healthy_is_reported_by_name_and_nothing_is_rolled_back()
    {
        var rig = new Rig();
        await rig.SeedAsync();
        rig.Remote.Probe = (svc, _, _) => svc == "api" ? "exited||0" : "running|healthy|0";

        var r = await rig.Restart();
        Assert.AreEqual(JitOutcome.RestartFailed, r.Outcome);
        Assert.IsFalse(r.Success);
        StringAssert.Contains(r.Message, "api");
        Assert.AreEqual("r1-aaaaaaa", rig.Remote.ReadState().CurrentTag, "a restart is not a release, so it is never rolled back");
        Assert.AreEqual(DeployPhase.Idle, rig.Remote.ReadState().Phase);
    }

    [TestMethod]
    public async Task A_dropped_connection_is_reported_as_such()
    {
        var rig = new Rig();
        await rig.SeedAsync();
        rig.Remote.ThrowWhen = c => c.Contains(" down ");
        var r = await rig.Restart();
        Assert.AreEqual(JitOutcome.ConnectionLost, r.Outcome);
    }

    [TestMethod]
    public async Task Only_one_operation_per_project_at_a_time()
    {
        var rig = new Rig();
        await rig.SeedAsync();
        using var held = rig.Lock.TryAcquire(JitDeployLock.KeyFor(Target));
        var r = await rig.Restart();
        Assert.AreEqual(JitOutcome.Busy, r.Outcome);
    }
}
