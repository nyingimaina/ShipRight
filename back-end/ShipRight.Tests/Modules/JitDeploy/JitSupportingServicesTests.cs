using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.JitDeploy;

namespace ShipRight.Tests.Modules.JitDeploy;

/// <summary>
/// A first deploy on a fresh server also starts the services ShipRight does not build (database, proxy, ...): the ones the app
/// needs before it, the ones that need the app after it. Later deploys leave them exactly as they are.
/// </summary>
[TestClass]
public class JitSupportingServicesTests
{
    private static readonly JitTarget Target = new("h", "u", "/k", "/home/deploy/shop");

    private static JitDeployRequest Req() => new()
    {
        Target = Target, Project = "shop", RepoUrl = "git@github.com:o/r.git", Branch = "master",
        Services = [new JitService("api", "API_TAG", "shop/api"), new JitService("web", "WEB_TAG", "shop/web")],
    };

    private sealed class Rig
    {
        public FakeRemote Remote { get; } = new();
        public List<string> Lines { get; } = [];
        public JitDeployer Deployer => new(Remote, new FakeClock(), new JitDeployLock());
        public Task<JitDeployResult> Deploy() => Deployer.DeployAsync(Req(), l => { lock (Lines) Lines.Add(l); return Task.CompletedTask; });
        public string All => string.Join("\n", Lines);
        public int At(string e) => Remote.Events.IndexOf(e);
    }

    [TestMethod]
    public async Task A_first_deploy_starts_the_database_before_the_app_that_needs_it()
    {
        var rig = new Rig();
        var r = await rig.Deploy();
        Assert.AreEqual(JitOutcome.Deployed, r.Outcome, r.Message);

        Assert.IsTrue(rig.At("start:db@16") >= 0, string.Join(" | ", rig.Remote.Events));
        Assert.IsTrue(rig.At("build:api,web") < rig.At("start:db@16"), "built first, so a failed build starts nothing");
        Assert.IsTrue(rig.At("start:db@16") < rig.At("start:api@r1-aaaaaaa"), "the database is up before the app");
        Assert.IsTrue(rig.Remote.Running.ContainsKey("db"));
        StringAssert.Contains(rig.All, "supporting service");
    }

    [TestMethod]
    public async Task Later_deploys_never_touch_the_supporting_services()
    {
        var rig = new Rig();
        await rig.Deploy();
        rig.Remote.Events.Clear();
        rig.Remote.Branches["master"] = FakeRemote.Sha2;
        await rig.Deploy();
        Assert.IsFalse(rig.Remote.Events.Any(e => e.StartsWith("start:db")), string.Join(" | ", rig.Remote.Events));
    }

    [TestMethod]
    public async Task Supporting_services_start_in_dependency_order()
    {
        var rig = new Rig();
        rig.Remote.Stack.Add(new FakeRemote.Svc("cache", ["db"], false, "redis:7", null));
        await rig.Deploy();
        Assert.IsTrue(rig.At("start:db@16") < rig.At("start:cache@7"), string.Join(" | ", rig.Remote.Events));
    }

    [TestMethod]
    public async Task A_service_that_needs_the_app_is_started_after_it()
    {
        var rig = new Rig();
        rig.Remote.Stack.Add(new FakeRemote.Svc("proxy", ["web"], false, "nginx:1", null));
        var r = await rig.Deploy();
        Assert.AreEqual(JitOutcome.Deployed, r.Outcome, r.Message);
        Assert.IsTrue(rig.At("start:web@r1-aaaaaaa") < rig.At("start:proxy@1"), string.Join(" | ", rig.Remote.Events));
    }

    [TestMethod]
    public async Task A_supporting_service_that_cannot_start_stops_a_first_deploy_before_the_app_is_touched()
    {
        var rig = new Rig();
        rig.Remote.StartFails = (svc, _) => svc == "db";
        var r = await rig.Deploy();
        Assert.AreEqual(JitOutcome.FailedBeforeChange, r.Outcome);
        StringAssert.Contains(r.Message, "db");
        Assert.IsFalse(rig.Remote.Events.Any(e => e.StartsWith("start:api") || e.StartsWith("stop:")), string.Join(" | ", rig.Remote.Events));
    }

    [TestMethod]
    public async Task A_supporting_service_that_is_not_healthy_is_reported_but_does_not_block_the_deploy()
    {
        var rig = new Rig();
        rig.Remote.Probe = (svc, _, _) => svc == "db" ? "exited||0" : "running|healthy|0";
        var r = await rig.Deploy();
        Assert.AreEqual(JitOutcome.Deployed, r.Outcome, r.Message);
        StringAssert.Contains(rig.All, "db");
        StringAssert.Contains(rig.All, "not healthy");
    }

    [TestMethod]
    public async Task A_dependent_the_person_chose_not_to_restart_is_still_started_on_a_first_deploy_and_failing_is_only_a_warning()
    {
        var rig = new Rig();
        rig.Remote.Stack.Add(new FakeRemote.Svc("proxy", ["web"], false, "nginx:1", null));
        rig.Remote.StartFails = (svc, _) => svc == "proxy";
        var req = Req() with { DependentChoices = new Dictionary<(string Service, string Dependent), bool> { [("web", "proxy")] = false } };
        var r = await rig.Deployer.DeployAsync(req, l => { lock (rig.Lines) rig.Lines.Add(l); return Task.CompletedTask; });
        Assert.AreEqual(JitOutcome.Deployed, r.Outcome, r.Message);
        StringAssert.Contains(rig.All, "proxy");
        StringAssert.Contains(rig.All, "Warning");
    }

    [TestMethod]
    public async Task A_dependent_that_restarts_with_the_app_is_started_by_the_normal_swap_and_a_failure_still_rolls_back()
    {
        var rig = new Rig();
        rig.Remote.Stack.Add(new FakeRemote.Svc("proxy", ["web"], false, "nginx:1", null));
        rig.Remote.StartFails = (svc, _) => svc == "proxy";
        var r = await rig.Deploy();
        Assert.AreNotEqual(JitOutcome.Deployed, r.Outcome, "this is the existing, release-level behaviour and it is unchanged");
    }

    [TestMethod]
    public async Task With_no_supporting_services_a_first_deploy_is_exactly_as_before()
    {
        var rig = new Rig();
        rig.Remote.Stack = [new FakeRemote.Svc("api", [], true, "shop/api", "API_TAG"), new FakeRemote.Svc("web", ["api"], true, "shop/web", "WEB_TAG")];
        var r = await rig.Deploy();
        Assert.AreEqual(JitOutcome.Deployed, r.Outcome, r.Message);
        Assert.IsFalse(rig.All.Contains("supporting service"));
    }
}
