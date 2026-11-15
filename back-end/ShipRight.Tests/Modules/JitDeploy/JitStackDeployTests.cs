using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.JitDeploy;

namespace ShipRight.Tests.Modules.JitDeploy;

/// <summary>The stack folder inside real deploys: made after a release is live, checked against ShipRight's own rendering, and never able to fail a deploy.</summary>
[TestClass]
public class JitStackDeployTests
{
    private static readonly JitTarget Target = new("h", "u", "/k", "/home/deploy/shop");

    private static JitDeployRequest Req(string? composeFile = null, string? env = null) => new()
    {
        Target = Target, Project = "shop", RepoUrl = "git@github.com:o/r.git", Branch = "master",
        Services = [new JitService("api", "API_TAG", "shop/api"), new JitService("web", "WEB_TAG", "shop/web")],
        ComposeFile = composeFile, EnvContent = env,
    };

    private sealed class Rig
    {
        public FakeRemote Remote { get; } = new();
        public JitDeployLock Lock { get; } = new();
        public List<string> Lines { get; } = [];
        public JitDeployer Deployer => new(Remote, new FakeClock(), Lock);
        private Task Log(string l) { lock (Lines) Lines.Add(l); return Task.CompletedTask; }
        public Task<JitDeployResult> Deploy(JitDeployRequest? r = null) => Deployer.DeployAsync(r ?? Req(), Log);
        public Task<JitDeployResult> Restart(JitDeployRequest? r = null) => Deployer.RestartAllAsync(r ?? Req(), Log);
        public string Stack => string.Join("\n", Lines.Where(l => l.StartsWith("[stack]")));
    }

    [TestMethod]
    public async Task After_a_deploy_the_stack_folder_is_written_verified_and_explained()
    {
        var rig = new Rig();
        var r = await rig.Deploy(Req(env: "MARIADB_USER=app"));
        Assert.AreEqual(JitOutcome.Deployed, r.Outcome, r.Message);

        CollectionAssert.Contains(rig.Remote.Events, "stack-write");
        var yaml = rig.Remote.StackFiles["compose.yaml"];
        StringAssert.Contains(yaml, "name: shop");
        StringAssert.Contains(yaml, "/home/deploy/shop/repo/docker-compose.yml");
        var env = rig.Remote.StackFiles[".env"];
        StringAssert.Contains(env, "MARIADB_USER=app");
        StringAssert.Contains(env, "API_TAG=shop/api:r1-aaaaaaa");
        StringAssert.Contains(rig.Remote.StackFiles["dc"], "docker compose -p 'shop'");

        StringAssert.Contains(rig.Stack, "no extra options");
        StringAssert.Contains(rig.Stack, "/home/deploy/shop/stack");
        CollectionAssert.DoesNotContain(rig.Remote.Events, "stack-remove");
    }

    [TestMethod]
    public async Task A_compose_file_chosen_in_the_settings_is_used_without_looking_for_one()
    {
        var rig = new Rig();
        await rig.Deploy(Req(composeFile: "deploy/stack.yml"));
        StringAssert.Contains(rig.Remote.StackFiles["compose.yaml"], "/home/deploy/shop/repo/deploy/stack.yml");
        CollectionAssert.DoesNotContain(rig.Remote.Events, "stack-detect");
    }

    [TestMethod]
    public async Task Without_a_compose_file_in_the_repo_nothing_is_written_and_the_deploy_is_fine()
    {
        var rig = new Rig { Remote = { NoComposeFile = true } };
        var r = await rig.Deploy();
        Assert.AreEqual(JitOutcome.Deployed, r.Outcome, r.Message);
        CollectionAssert.DoesNotContain(rig.Remote.Events, "stack-write");
        StringAssert.Contains(rig.Stack, "No compose file");
    }

    [TestMethod]
    public async Task A_compose_that_is_too_old_gets_the_helper_only_and_an_explanation()
    {
        var rig = new Rig { Remote = { StackConfigFails = true, StackVersion = "2.10.2" } };
        var r = await rig.Deploy();
        Assert.AreEqual(JitOutcome.Deployed, r.Outcome, r.Message);
        CollectionAssert.Contains(rig.Remote.Events, "stack-remove");
        StringAssert.Contains(rig.Stack, "2.20");
        StringAssert.Contains(rig.Stack, "2.10.2");
        StringAssert.Contains(rig.Stack, "/home/deploy/shop/stack/dc");
    }

    [TestMethod]
    public async Task A_shortcut_that_renders_differently_is_removed_and_the_services_that_differ_are_named()
    {
        var rig = new Rig { Remote = { StackConfigJson = "{\"name\":\"shop\",\"services\":{\"api\":{\"image\":\"other\"}}}" } };
        await rig.Deploy();
        CollectionAssert.Contains(rig.Remote.Events, "stack-remove");
        StringAssert.Contains(rig.Stack, "differs in");
        StringAssert.Contains(rig.Stack, "api");
    }

    [TestMethod]
    public async Task A_file_ShipRight_did_not_write_is_left_alone_and_the_deploy_still_succeeds()
    {
        var rig = new Rig { Remote = { StackWriteBlocked = true } };
        var r = await rig.Deploy();
        Assert.AreEqual(JitOutcome.Deployed, r.Outcome, r.Message);
        StringAssert.Contains(rig.Stack, "not managed by ShipRight");
    }

    [TestMethod]
    public async Task Whatever_goes_wrong_with_the_stack_folder_never_fails_a_deploy()
    {
        var rig = new Rig();
        rig.Remote.ThrowWhen = c => c.Contains("'stack:");
        var r = await rig.Deploy();
        Assert.AreEqual(JitOutcome.Deployed, r.Outcome, r.Message);
    }

    [TestMethod]
    public async Task After_an_automatic_rollback_the_stack_points_at_the_release_that_is_running_again()
    {
        var rig = new Rig();
        await rig.Deploy();
        rig.Remote.Branches["master"] = FakeRemote.Sha2;
        rig.Remote.StartFails = (svc, tag) => svc == "api" && tag == "r2-bbbbbbb";
        rig.Remote.StackWrites.Clear();

        var r = await rig.Deploy();
        Assert.AreEqual(JitOutcome.RolledBack, r.Outcome, r.Message);
        Assert.IsTrue(rig.Remote.StackWrites.Count > 0, "the stack is refreshed after the rollback");
        var last = rig.Remote.StackWrites.Last();
        StringAssert.Contains(last[".env"], "API_TAG=shop/api:r1-aaaaaaa");
        Assert.IsFalse(rig.Remote.StackWrites.Any(w => w[".env"].Contains("r2-bbbbbbb")), "the failed release is never written");
    }

    [TestMethod]
    public async Task A_restart_refreshes_the_stack_too()
    {
        var rig = new Rig();
        await rig.Deploy();
        rig.Remote.StackWrites.Clear();
        var r = await rig.Restart(Req(env: "NEW=1"));
        Assert.AreEqual(JitOutcome.Restarted, r.Outcome, r.Message);
        StringAssert.Contains(rig.Remote.StackWrites.Last()[".env"], "NEW=1");
    }
}
