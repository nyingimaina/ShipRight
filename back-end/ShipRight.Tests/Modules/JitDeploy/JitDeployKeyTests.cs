using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.JitDeploy;
using ShipRight.Modules.Projects;
using ShipRight.Shared.SshRunner;

namespace ShipRight.Tests.Modules.JitDeploy;

/// <summary>
/// A read-only SSH key made on the server itself, so the server can read a private repository without any secret ever leaving
/// the machine. Nothing here names the deployer; the key and the way git is told to use it are ordinary.
/// </summary>
[TestClass]
public class JitDeployKeyTests
{
    private const string Pub = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIexampleexampleexampleexampleexampleexam deploy-shop@box";

    // ── names and commands ───────────────────────────────────────────────────

    [TestMethod]
    public void TheKeyIsNamedFromTheStack_InTheUsersSshFolder()
    {
        Assert.AreEqual("deploy_shop", JitDeployKey.KeyName("shop"));
        Assert.AreEqual("deploy_my-app_2", JitDeployKey.KeyName("my-app_2"));
        Assert.ThrowsException<ArgumentException>(() => JitDeployKey.KeyName("Bad Name"));
        Assert.ThrowsException<ArgumentException>(() => JitDeployKey.KeyName("a;b"));
    }

    [TestMethod]
    public void TheEnsureCommand_CreatesOnlyWhenMissing_AndPrintsThePublicKey()
    {
        var c = JitDeployKey.EnsureCommand("shop");
        StringAssert.Contains(c, "~/.ssh/deploy_shop");
        StringAssert.Contains(c, "if [ ! -f");
        StringAssert.Contains(c, "ssh-keygen -t ed25519 -N ''");
        StringAssert.Contains(c, "cat ");
        Assert.IsFalse(c.Contains("rm ") || c.Contains("-y "), "it never removes or overwrites a key");
        Assert.IsFalse(c.Contains("shipright", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void ThePublicKeyIsRecognised_AmongOtherOutput()
    {
        Assert.AreEqual(Pub, JitDeployKey.ParsePublicKey(["[deploykey] created", Pub, ""]));
        Assert.IsNull(JitDeployKey.ParsePublicKey(["[deploykey] created", "ssh-ed25519 not a key!"]));
        Assert.IsNull(JitDeployKey.ParsePublicKey([]));
    }

    [TestMethod]
    public void GitIsToldToUseTheKey_OnlyForThatCommand_AndNeverToAskForAPassword()
    {
        var p = JitDeployKey.GitSshPrefix("deploy_shop");
        StringAssert.StartsWith(p, "export GIT_SSH_COMMAND='ssh ");
        StringAssert.Contains(p, "-i \"$HOME/.ssh/deploy_shop\"");
        StringAssert.Contains(p, "IdentitiesOnly=yes");
        StringAssert.Contains(p, "BatchMode=yes");
        StringAssert.Contains(p, "StrictHostKeyChecking=accept-new");
        StringAssert.EndsWith(p, "; ");
        Assert.ThrowsException<ArgumentException>(() => JitDeployKey.GitSshPrefix("bad name"));
    }

    // ── where to paste it ────────────────────────────────────────────────────

    [DataTestMethod]
    [DataRow("git@github.com:Org/Repo.git", "GitHub", "https://github.com/Org/Repo/settings/keys")]
    [DataRow("https://github.com/Org/Repo", "GitHub", "https://github.com/Org/Repo/settings/keys")]
    [DataRow("git@gitlab.com:g/sub/r.git", "GitLab", "https://gitlab.com/g/sub/r/-/settings/repository")]
    [DataRow("git@bitbucket.org:o/r.git", "Bitbucket", "https://bitbucket.org/o/r/admin/access-keys/")]
    public void TheDeployKeyPage_IsWorkedOutForTheCommonHosts(string url, string label, string page)
    {
        var h = JitDeployKey.HostPage(url);
        Assert.AreEqual(label, h.Label);
        Assert.AreEqual(page, h.Url);
    }

    [TestMethod]
    public void AnUnknownHost_HasNoPage_ButStillALabel()
    {
        var h = JitDeployKey.HostPage("git@git.example.com:team/app.git");
        Assert.AreEqual("git.example.com", h.Label);
        Assert.IsNull(h.Url);
        Assert.IsNull(JitDeployKey.HostPage("").Url);
    }

    // ── real shell ───────────────────────────────────────────────────────────

    private static string? Bash()
    {
        foreach (var p in new[] { @"C:\Program Files\Git\bin\bash.exe", "/bin/bash", "/usr/bin/bash" })
            if (File.Exists(p)) return p;
        return null;
    }

    private static (int Exit, string Out, string Err) Run(string args, string stdin)
    {
        var psi = new ProcessStartInfo(Bash()!, args) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        p.StandardInput.Write(stdin);
        p.StandardInput.Close();
        var o = p.StandardOutput.ReadToEnd(); var e = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, o, e);
    }

    [TestMethod]
    public void TheCommandsParseAsRealShell()
    {
        if (Bash() is null) Assert.Inconclusive("bash is not available.");
        foreach (var c in new[] { JitDeployKey.EnsureCommand("shop"), JitDeployKey.GitSshPrefix("deploy_shop") + "true" })
            Assert.AreEqual(0, Run("-n", c).Exit, c);
    }

    [TestMethod]
    public void EnsuringTheKeyTwice_InRealShell_MakesOneKey_AndReturnsTheSameOne()
    {
        if (Bash() is null) Assert.Inconclusive("bash is not available.");
        var home = Path.Combine(Path.GetTempPath(), "dk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        try
        {
            var script = $"export HOME='{home.Replace('\\', '/')}'; {JitDeployKey.EnsureCommand("shop")}";
            var (e1, o1, err1) = Run("-s", script);
            if (e1 != 0 && err1.Contains("ssh-keygen")) Assert.Inconclusive("ssh-keygen is not available.");
            Assert.AreEqual(0, e1, err1);
            var (e2, o2, _) = Run("-s", script);
            Assert.AreEqual(0, e2);
            Assert.AreEqual(JitDeployKey.ParsePublicKey(o1.Split('\n')), JitDeployKey.ParsePublicKey(o2.Split('\n')));
            Assert.IsNotNull(JitDeployKey.ParsePublicKey(o1.Split('\n')));
            StringAssert.Contains(o1, "created");
            StringAssert.Contains(o2, "already");
        }
        finally { try { Directory.Delete(home, true); } catch { /* best effort */ } }
    }

    // ── the deploy itself uses the key, only for the commands that reach the git host ──

    private static readonly JitTarget Target = new("h", "u", "/k", "/home/deploy/shop");

    private static JitDeployRequest Req(string? keyName) => new()
    {
        Target = Target, Project = "shop", RepoUrl = "git@github.com:o/r.git", Branch = "master", DeployKeyName = keyName,
        Services = [new JitService("api", "API_TAG", "shop/api"), new JitService("web", "WEB_TAG", "shop/web")],
    };

    private static async Task<FakeRemote> Deploy(string? keyName)
    {
        var remote = new FakeRemote();
        var r = await new JitDeployer(remote, new FakeClock(), new JitDeployLock()).DeployAsync(Req(keyName), _ => Task.CompletedTask);
        Assert.AreEqual(JitOutcome.Deployed, r.Outcome, r.Message);
        return remote;
    }

    [TestMethod]
    public async Task WithADeployKey_TheCheckAndTheSyncUseIt_AndNothingElseDoes()
    {
        var remote = await Deploy("deploy_shop");
        var withKey = remote.Commands.Where(c => c.StartsWith("export GIT_SSH_COMMAND=")).ToList();
        Assert.AreEqual(2, withKey.Count, string.Join("\n", remote.Commands.Where(c => c.Contains("git"))));
        Assert.IsTrue(withKey.Any(c => c.Contains("git ls-remote")));
        Assert.IsTrue(withKey.Any(c => c.Contains("git clone") || c.Contains("fetch --prune")));
        Assert.IsFalse(remote.Commands.Any(c => c.Contains("docker") && c.Contains("GIT_SSH_COMMAND")));
    }

    [TestMethod]
    public async Task WithoutADeployKey_NothingChanges() =>
        Assert.IsFalse((await Deploy(null)).Commands.Any(c => c.Contains("GIT_SSH_COMMAND")));

    [TestMethod]
    public async Task AnUnsafeKeyName_IsRefusedBeforeAnythingIsSent()
    {
        var remote = new FakeRemote();
        var r = await new JitDeployer(remote, new FakeClock(), new JitDeployLock()).DeployAsync(Req("bad name; rm -rf /"), _ => Task.CompletedTask);
        Assert.AreEqual(JitOutcome.FailedBeforeChange, r.Outcome);
        Assert.AreEqual(0, remote.Commands.Count);
    }

    [TestMethod]
    public void TheSavedSetting_TurnsIntoTheKeyNameOfTheStack()
    {
        var project = new ProjectConfig
        {
            Id = "p1", Name = "Shop Front",
            Services = [new ServiceConfig { Name = "api", DockerImageName = "shop/api" }],
            Server = new ServerConfig { Host = "h", Username = "u", SshKeyPath = "/k", RemoteWorkingDir = "/home/u/shop" },
        };
        var on = JitDeployMapper.Map(project, new JitDeployConfig { Enabled = true, RepoUrl = "git@h:o/r.git", Branch = "main", UseDeployKey = true });
        var off = JitDeployMapper.Map(project, new JitDeployConfig { Enabled = true, RepoUrl = "git@h:o/r.git", Branch = "main" });
        Assert.AreEqual("deploy_shop-front", on.Request!.DeployKeyName);
        Assert.IsNull(off.Request!.DeployKeyName);
    }

    // ── the endpoints ────────────────────────────────────────────────────────

    private sealed class MemoryProjects : IProjectStore
    {
        public Dictionary<string, ProjectConfig> All { get; } = [];
        public int Count => All.Count;
        public Task<ProjectConfig?> GetByNameAsync(string name) => Task.FromResult(All.Values.FirstOrDefault(p => p.Name == name));
        public Task<List<ProjectConfig>> GetAllAsync() => Task.FromResult(All.Values.ToList());
        public Task<ProjectConfig?> GetByIdAsync(string id) => Task.FromResult(All.GetValueOrDefault(id));
        public Task SaveAsync(ProjectConfig p) { All[p.Id] = p; return Task.CompletedTask; }
        public Task DeleteAsync(string id) { All.Remove(id); return Task.CompletedTask; }
    }

    private sealed class MemoryConfigs : IJitConfigStore
    {
        public Task<JitDeployConfig?> GetAsync(string id) => Task.FromResult<JitDeployConfig?>(null);
        public Task SaveAsync(string id, JitDeployConfig c) => Task.CompletedTask;
        public Task DeleteAsync(string id) => Task.CompletedTask;
    }

    private sealed class FakeSsh : ISshRunner
    {
        public string[] Output { get; set; } = [];
        public int Exit { get; set; }
        public Exception? Throw { get; set; }
        public List<string> Commands { get; } = [];
        public async Task<int> RunAsync(string host, string username, string keyPath, string command, Func<string, Task>? onOutput = null, Func<string, Task>? onStderr = null, CancellationToken ct = default)
        {
            Commands.Add(command);
            if (Throw is not null) throw Throw;
            if (onOutput is not null) foreach (var l in Output) await onOutput(l);
            return Exit;
        }
    }

    private static ProjectConfig WithServer(bool server = true) => new()
    {
        Id = "p1", Name = "Shop",
        Server = server ? new ServerConfig { Host = "h", Username = "u", SshKeyPath = "/k", RemoteWorkingDir = "/home/u/shop" } : new ServerConfig(),
    };

    private static async Task<MemoryProjects> Store(ProjectConfig p) { var s = new MemoryProjects(); await s.SaveAsync(p); return s; }
    private static int Status(IResult r) => ((IStatusCodeHttpResult)r).StatusCode ?? 200;
    private static string Json(IResult r) => JsonSerializer.Serialize(((IValueHttpResult)r).Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    [TestMethod]
    public async Task TheKeyEndpoint_ReturnsThePublicKey_AndWhereToPasteIt()
    {
        var ssh = new FakeSsh { Output = ["[deploykey] created", Pub] };
        var r = await JitDeployKeyRoutes.HandleCreateKeyAsync("p1", new DeployKeyRequest("git@github.com:o/r.git"), await Store(WithServer()), new MemoryConfigs(), ssh);

        Assert.AreEqual(200, Status(r));
        var json = Json(r);
        StringAssert.Contains(json, "ssh-ed25519 AAAAC3");
        StringAssert.Contains(json, "\"created\":true");
        StringAssert.Contains(json, "GitHub");
        StringAssert.Contains(json, "https://github.com/o/r/settings/keys");
        StringAssert.Contains(json, "\"keyName\":\"deploy_shop\"");
        Assert.AreEqual(JitDeployKey.EnsureCommand("shop"), ssh.Commands.Single());
    }

    [TestMethod]
    public async Task AnExistingKey_IsReturnedNotReplaced()
    {
        var ssh = new FakeSsh { Output = ["[deploykey] already there", Pub] };
        var r = await JitDeployKeyRoutes.HandleCreateKeyAsync("p1", new DeployKeyRequest(null), await Store(WithServer()), new MemoryConfigs(), ssh);
        StringAssert.Contains(Json(r), "\"created\":false");
    }

    [TestMethod]
    public async Task TheKeyEndpoint_UnknownProject_NoServer_OrAServerThatFails_AreAllPlain()
    {
        Assert.AreEqual(404, Status(await JitDeployKeyRoutes.HandleCreateKeyAsync("x", new DeployKeyRequest(null), new MemoryProjects(), new MemoryConfigs(), new FakeSsh())));
        Assert.AreEqual(400, Status(await JitDeployKeyRoutes.HandleCreateKeyAsync("p1", new DeployKeyRequest(null), await Store(WithServer(false)), new MemoryConfigs(), new FakeSsh())));

        var failed = await JitDeployKeyRoutes.HandleCreateKeyAsync("p1", new DeployKeyRequest(null), await Store(WithServer()), new MemoryConfigs(), new FakeSsh { Exit = 1, Output = ["[deploykey] Could not make a key on the server."] });
        Assert.AreEqual(200, Status(failed));
        StringAssert.Contains(Json(failed), "Could not make a key");

        var lost = await JitDeployKeyRoutes.HandleCreateKeyAsync("p1", new DeployKeyRequest(null), await Store(WithServer()), new MemoryConfigs(), new FakeSsh { Throw = new IOException("gone") });
        StringAssert.Contains(Json(lost), "Lost contact");
    }

    [TestMethod]
    public async Task TheAccessCheck_SaysYes_WhenTheServerCanReadTheRepo_UsingTheKeyWhenAsked()
    {
        var ssh = new FakeSsh();
        var r = await JitDeployKeyRoutes.HandleGitAccessAsync("p1", new GitAccessRequest("git@github.com:o/r.git", true), await Store(WithServer()), new MemoryConfigs(), ssh);

        Assert.AreEqual(200, Status(r));
        StringAssert.Contains(Json(r), "\"ok\":true");
        StringAssert.StartsWith(ssh.Commands.Single(), "export GIT_SSH_COMMAND=");
        StringAssert.Contains(ssh.Commands.Single(), "deploy_shop");
    }

    [TestMethod]
    public async Task TheAccessCheck_WithoutTheKey_UsesThePlainCommand()
    {
        var ssh = new FakeSsh();
        await JitDeployKeyRoutes.HandleGitAccessAsync("p1", new GitAccessRequest("git@github.com:o/r.git", false), await Store(WithServer()), new MemoryConfigs(), ssh);
        Assert.AreEqual(JitCommands.CheckGit("git@github.com:o/r.git"), ssh.Commands.Single());
    }

    [TestMethod]
    public async Task TheAccessCheck_SaysWhyItFailed_WithoutTheAddress()
    {
        var ssh = new FakeSsh { Exit = 1, Output = ["[preflight] Cannot reach the git repository. Check the address, the network, and that this server has access (a deploy key or token)."] };
        var r = await JitDeployKeyRoutes.HandleGitAccessAsync("p1", new GitAccessRequest("git@github.com:secret-org/r.git", false), await Store(WithServer()), new MemoryConfigs(), ssh);
        var json = Json(r);
        StringAssert.Contains(json, "\"ok\":false");
        StringAssert.Contains(json, "Cannot reach the git repository");
        Assert.IsFalse(json.Contains("secret-org"));
    }

    [TestMethod]
    public async Task TheAccessCheck_RefusesBadAddresses_AndSecrets_BeforeContactingTheServer()
    {
        var ssh = new FakeSsh();
        var store = await Store(WithServer());
        Assert.AreEqual(400, Status(await JitDeployKeyRoutes.HandleGitAccessAsync("p1", new GitAccessRequest("--upload-pack=x", false), store, new MemoryConfigs(), ssh)));
        Assert.AreEqual(400, Status(await JitDeployKeyRoutes.HandleGitAccessAsync("p1", new GitAccessRequest("https://me:pw@github.com/o/r.git", false), store, new MemoryConfigs(), ssh)));
        Assert.AreEqual(400, Status(await JitDeployKeyRoutes.HandleGitAccessAsync("p1", new GitAccessRequest("", false), store, new MemoryConfigs(), ssh)));
        Assert.AreEqual(0, ssh.Commands.Count);
    }
}
