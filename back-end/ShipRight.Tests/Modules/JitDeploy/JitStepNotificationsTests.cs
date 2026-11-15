using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.JitDeploy;

namespace ShipRight.Tests.Modules.JitDeploy;

/// <summary>Each finished step is announced on its own line, "[step] ✓ what (seconds)", so the screen can show progress as it happens.</summary>
[TestClass]
public class JitStepNotificationsTests
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
        public Task<JitDeployResult> Restart() => Deployer.RestartAllAsync(Req(), l => { lock (Lines) Lines.Add(l); return Task.CompletedTask; });
        public List<string> Steps => Lines.Where(l => l.StartsWith("[step] ✓ ")).ToList();
    }

    private static void InOrder(List<string> steps, params string[] fragments)
    {
        var at = -1;
        foreach (var f in fragments)
        {
            var i = steps.FindIndex(at + 1, s => s.Contains(f, StringComparison.OrdinalIgnoreCase));
            Assert.IsTrue(i > at, $"'{f}' was not announced after the previous step. Steps: {string.Join(" | ", steps)}");
            at = i;
        }
    }

    [TestMethod]
    public async Task A_deploy_announces_each_step_when_it_finishes_in_order()
    {
        var rig = new Rig();
        var r = await rig.Deploy();
        Assert.AreEqual(JitOutcome.Deployed, r.Outcome, r.Message);
        InOrder(rig.Steps, "Server ready", "Server checked", "Code fetched", "Compose file read", "Built api, web", "supporting", "api is running", "web is running", "Release r1-aaaaaaa is live");
    }

    [TestMethod]
    public async Task Every_step_line_carries_how_long_it_took()
    {
        var rig = new Rig();
        await rig.Deploy();
        Assert.IsTrue(rig.Steps.Count >= 6);
        foreach (var s in rig.Steps.Where(s => !s.Contains("is live")))
            StringAssert.Matches(s, new Regex(@"\(\d+\.\d s\)$"), s);
    }

    [TestMethod]
    public async Task A_failed_step_is_never_announced_as_done()
    {
        var rig = new Rig { Remote = { BuildOk = false } };
        var r = await rig.Deploy();
        Assert.AreEqual(JitOutcome.FailedBeforeChange, r.Outcome);
        Assert.IsFalse(rig.Steps.Any(s => s.Contains("Built")), string.Join(" | ", rig.Steps));
        Assert.IsTrue(rig.Steps.Any(s => s.Contains("Code fetched")), "the steps that did finish are still announced");
    }

    [TestMethod]
    public async Task A_restart_announces_its_steps_too()
    {
        var rig = new Rig();
        await rig.Deploy();
        rig.Lines.Clear();
        var r = await rig.Restart();
        Assert.AreEqual(JitOutcome.Restarted, r.Outcome, r.Message);
        InOrder(rig.Steps, "images are ready", "All services stopped", "All services started", "Every service is healthy");
    }

    [TestMethod]
    public async Task Step_lines_never_contain_secrets_or_the_git_address()
    {
        var rig = new Rig();
        await rig.Deploy();
        foreach (var s in rig.Steps) Assert.IsFalse(s.Contains("github.com") || s.Contains("git@"), s);
    }
}
