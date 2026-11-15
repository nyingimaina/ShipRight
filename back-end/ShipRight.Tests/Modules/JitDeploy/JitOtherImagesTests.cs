using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.JitDeploy;

namespace ShipRight.Tests.Modules.JitDeploy;

/// <summary>
/// Services ShipRight does not build (a database, a proxy) still need their images on the server. They are downloaded, or built,
/// BEFORE anything is stopped, so a restart can never take the site down and then fail to bring it back.
/// </summary>
[TestClass]
public class JitOtherImagesTests
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
        private Task Log(string l) { lock (Lines) Lines.Add(l); return Task.CompletedTask; }
        public Task<JitDeployResult> Deploy() => Deployer.DeployAsync(Req(), Log);
        public Task<JitDeployResult> Restart() => Deployer.RestartAllAsync(Req(), Log);
        public string All => string.Join("\n", Lines);
    }

    // ── the commands ──────────────────────────────────────────────────────────

    private static readonly string? Bash = new[]
    {
        "/bin/bash", "/usr/bin/bash", @"C:\Program Files\Git\bin\bash.exe", @"C:\Program Files\Git\usr\bin\bash.exe",
    }.FirstOrDefault(File.Exists);

    private static (int Exit, string Out, string Err) Run(string args, string stdin)
    {
        if (Bash is null) Assert.Inconclusive("No POSIX bash on this machine.");
        var psi = new ProcessStartInfo(Bash!, args) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi)!;
        p.StandardInput.Write(stdin); p.StandardInput.Close();
        var o = p.StandardOutput.ReadToEnd(); var e = p.StandardError.ReadToEnd();
        p.WaitForExit(15000);
        return (p.ExitCode, o, e);
    }

    [TestMethod]
    public void The_missing_image_check_lists_only_what_is_absent_and_is_valid_shell()
    {
        var c = JitCommands.MissingImages(["mariadb:11.4", "nginxproxy/nginx-proxy:latest"]);
        StringAssert.StartsWith(c, ": 'images:missing'; ");
        StringAssert.Contains(c, "'mariadb:11.4'");
        var (exit, _, err) = Run("-n", c);
        Assert.AreEqual(0, exit, err);
        CollectionAssert.AreEqual(new[] { "mariadb:11.4" }, JitCommands.ParseMissingImages(["missing:mariadb:11.4", "noise", ""]).ToArray());
    }

    [DataTestMethod]
    [DataRow("a b")]
    [DataRow("x;rm -rf /")]
    [DataRow("$(id)")]
    [DataRow("`id`")]
    [DataRow("")]
    public void The_missing_image_check_refuses_anything_that_is_not_an_image_reference(string image)
    {
        Assert.ThrowsException<ArgumentException>(() => JitCommands.MissingImages([image]));
    }

    [TestMethod]
    public void Pulling_names_only_the_services_asked_for_and_never_removes_anything()
    {
        var ctx = new ComposeContext(Target, "shop", null, null, null);
        var c = JitCommands.PullServices(ctx, [new("API_TAG", "shop/api")], "r1-aaaaaaa", ["db", "proxy"]);
        StringAssert.Contains(c, "docker compose -p 'shop'");
        StringAssert.Contains(c, " pull 'db' 'proxy'");
        Assert.IsFalse(c.Contains("down") || c.Contains(" rm ") || c.Contains("prune"));
    }

    // ── restart all ───────────────────────────────────────────────────────────

    private static async Task<Rig> SeededAsync()
    {
        var rig = new Rig();
        var r = await rig.Deploy();
        Assert.AreEqual(JitOutcome.Deployed, r.Outcome, r.Message);
        rig.Remote.Events.Clear(); rig.Remote.Commands.Clear(); rig.Lines.Clear();
        return rig;
    }

    [TestMethod]
    public async Task A_restart_downloads_missing_third_party_images_before_stopping_anything()
    {
        var rig = await SeededAsync();
        rig.Remote.ThirdPartyMissing.Add("postgres:16");

        var r = await rig.Restart();
        Assert.AreEqual(JitOutcome.Restarted, r.Outcome, r.Message);
        var e = rig.Remote.Events;
        Assert.IsTrue(e.IndexOf("pull:db") >= 0 && e.IndexOf("pull:db") < e.IndexOf("down"), string.Join(" | ", e));
        StringAssert.Contains(rig.All, "Downloading");
        StringAssert.Contains(rig.All, "postgres:16");
        Assert.IsTrue(rig.Remote.Running.ContainsKey("db"));
    }

    [TestMethod]
    public async Task A_restart_does_not_download_what_is_already_there()
    {
        var rig = await SeededAsync();
        await rig.Restart();
        Assert.IsFalse(rig.Remote.Events.Any(x => x.StartsWith("pull:")));
    }

    [TestMethod]
    public async Task If_an_image_cannot_be_downloaded_nothing_is_stopped()
    {
        var rig = await SeededAsync();
        rig.Remote.ThirdPartyMissing.Add("postgres:16");
        rig.Remote.PullFails = true;
        var runningBefore = rig.Remote.Running.Count;

        var r = await rig.Restart();
        Assert.AreEqual(JitOutcome.FailedBeforeChange, r.Outcome);
        StringAssert.Contains(r.Message, "postgres:16");
        StringAssert.Contains(r.Message, "Nothing was stopped");
        CollectionAssert.DoesNotContain(rig.Remote.Events, "down");
        Assert.AreEqual(runningBefore, rig.Remote.Running.Count, "the running services are untouched");
    }

    [TestMethod]
    public async Task A_service_ShipRight_does_not_manage_but_that_has_a_build_is_built_first_when_its_image_is_missing()
    {
        var rig = await SeededAsync();
        rig.Remote.Stack.Add(new FakeRemote.Svc("worker", [], true, "shop/worker:latest", null));
        rig.Remote.ThirdPartyMissing.Add("shop/worker:latest");

        var r = await rig.Restart();
        Assert.AreEqual(JitOutcome.Restarted, r.Outcome, r.Message);
        var e = rig.Remote.Events;
        Assert.IsTrue(e.IndexOf("build:worker") >= 0 && e.IndexOf("build:worker") < e.IndexOf("down"), string.Join(" | ", e));
    }

    // ── a first deploy on a fresh server ──────────────────────────────────────

    [TestMethod]
    public async Task A_first_deploy_downloads_the_third_party_images_too()
    {
        var rig = new Rig();
        rig.Remote.ThirdPartyMissing.Add("postgres:16");
        var r = await rig.Deploy();
        Assert.AreEqual(JitOutcome.Deployed, r.Outcome, r.Message);
        CollectionAssert.Contains(rig.Remote.Events, "pull:db");
        StringAssert.Contains(rig.All, "postgres:16");
    }

    [TestMethod]
    public async Task A_first_deploy_carries_on_when_a_download_fails_and_says_how_to_finish()
    {
        var rig = new Rig();
        rig.Remote.ThirdPartyMissing.Add("postgres:16");
        rig.Remote.PullFails = true;
        var r = await rig.Deploy();
        Assert.AreEqual(JitOutcome.Deployed, r.Outcome, r.Message);
        StringAssert.Contains(rig.All, "Could not download");
        StringAssert.Contains(rig.All, "Restart all services");
    }

    [TestMethod]
    public async Task Later_deploys_do_not_look_for_other_images_at_all()
    {
        var rig = await SeededAsync();
        rig.Remote.Branches["master"] = FakeRemote.Sha2;
        await rig.Deploy();
        Assert.IsFalse(rig.Remote.Events.Contains("images-missing-check"));
    }
}
