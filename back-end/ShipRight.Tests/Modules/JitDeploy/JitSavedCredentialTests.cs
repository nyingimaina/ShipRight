using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.JitDeploy;
using ShipRight.Modules.Projects;
using ShipRight.Modules.Resources.Models;
using ShipRight.Modules.Resources.Stores;
using ShipRight.Shared.SshRunner;

namespace ShipRight.Tests.Modules.JitDeploy;

/// <summary>
/// A saved credential (a personal access token) can read a private https repository from the server without the token ever
/// being part of an address, a saved remote, the settings, a log or a message: it reaches git through a short-lived helper file
/// that exists for one command and is removed straight after.
/// </summary>
[TestClass]
public class JitSavedCredentialTests
{
    private const string Token = "AbC123_def-456xyzQWE789";
    private static readonly JitGitSecret Secret = new("token", Token);

    // ── the token and the user name ──────────────────────────────────────────

    [DataTestMethod]
    [DataRow("ghp_a1B2c3D4e5F6g7H8i9J0k1L2m3N4o5P6q7R8", true)]
    [DataRow("AbC123_def-456xyzQWE789", true)]
    [DataRow("short", false)]
    [DataRow("has space inside token", false)]
    [DataRow("quote'inside-the-token1", false)]
    [DataRow("line\nbreak-in-the-token1", false)]
    [DataRow("semi;colon-in-the-token1", false)]
    [DataRow("$(touch-pwned)-token-1234", false)]
    public void OnlyOrdinaryTokenCharactersAreAccepted(string token, bool ok) =>
        Assert.AreEqual(ok, JitGitCredential.TokenProblem(token) is null);

    [TestMethod]
    public void TheProblemMessage_NeverRepeatsTheToken()
    {
        var p = JitGitCredential.TokenProblem("quote'inside-the-token1")!;
        StringAssert.Contains(p, "characters");
        Assert.IsFalse(p.Contains("quote'inside"));
    }

    [TestMethod]
    public void TheUserName_IsDecidedByTheHost()
    {
        Assert.AreEqual("pat", JitGitCredential.UsernameFor("dev.azure.com", "https://dev.azure.com/o/p/_git/r"));
        Assert.AreEqual("pat", JitGitCredential.UsernameFor(null, "https://dev.azure.com/o/p/_git/r"));
        Assert.AreEqual("token", JitGitCredential.UsernameFor(null, "https://github.com/o/r.git"));
        Assert.AreEqual("token", JitGitCredential.UsernameFor("github.com", "https://github.com/o/r.git"));
    }

    [TestMethod]
    public void TheSecretNeverPrintsItself()
    {
        Assert.IsFalse(Secret.ToString().Contains(Token));
        Assert.IsFalse(JsonSerializer.Serialize(new { Secret = Secret.ToString() }).Contains(Token));
    }

    // ── the command ──────────────────────────────────────────────────────────

    [TestMethod]
    public void TheWrappedCommand_UsesAHelperThatIsRemoved_NeverStoresAnything_AndHoldsTheTokenOnce()
    {
        var c = JitGitCredential.Wrap("git ls-remote -- 'https://github.com/o/r.git'", Secret);

        Assert.AreEqual(1, c.Split(Token).Length - 1, "the token is in one place only");
        StringAssert.Contains(c, "mktemp -d");
        StringAssert.Contains(c, "chmod 700");
        StringAssert.Contains(c, "trap 'rm -rf \"$d\"' EXIT");
        StringAssert.Contains(c, "GIT_ASKPASS=");
        StringAssert.Contains(c, "GIT_TERMINAL_PROMPT=0");
        StringAssert.Contains(c, "GIT_CONFIG_KEY_0=credential.helper");
        StringAssert.Contains(c, "git ls-remote -- 'https://github.com/o/r.git'");
        Assert.IsFalse(c.Contains("git config") || c.Contains("credential store") || c.Contains(">> ~"), "nothing is written to git's own settings");
        Assert.IsFalse(c.Contains("shipright", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void AnUnusableToken_IsRefused_WithoutEchoingIt()
    {
        var ex = Assert.ThrowsException<ArgumentException>(() => JitGitCredential.Wrap("git status", new JitGitSecret("token", "bad'token-value-1234")));
        Assert.IsFalse(ex.Message.Contains("bad'token"));
        Assert.ThrowsException<ArgumentException>(() => JitGitCredential.Wrap("git status", new JitGitSecret("bad user", Token)));
    }

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
    public void TheCommandParsesAsRealShell()
    {
        if (Bash() is null) Assert.Inconclusive("bash is not available.");
        Assert.AreEqual(0, Run("-n", JitGitCredential.Wrap("git ls-remote -- 'https://github.com/o/r.git'", Secret)).Exit);
    }

    [TestMethod]
    public void InRealShell_GitCanAskForTheUserAndTheToken_ThenTheHelperIsGone()
    {
        if (Bash() is null) Assert.Inconclusive("bash is not available.");
        const string probe = "u=$(\"$GIT_ASKPASS\" \"Username for 'https://github.com': \"); p=$(\"$GIT_ASKPASS\" \"Password for 'https://token@github.com': \"); " +
                             "echo \"user=$u\"; echo \"pass=$p\"; echo \"path=$GIT_ASKPASS\"; echo \"prompt=$GIT_TERMINAL_PROMPT\"; echo \"helper=[$GIT_CONFIG_VALUE_0]\"";
        var (exit, output, err) = Run("-s", JitGitCredential.Wrap(probe, Secret));
        Assert.AreEqual(0, exit, err);
        StringAssert.Contains(output, "user=token");
        StringAssert.Contains(output, "pass=" + Token);
        StringAssert.Contains(output, "prompt=0");
        StringAssert.Contains(output, "helper=[]");

        var path = output.Split('\n').First(l => l.StartsWith("path=")).Trim()["path=".Length..];
        var (_, stillThere, _) = Run("-s", $"[ -e '{path}' ] && echo yes || echo no");
        Assert.AreEqual("no", stillThere.Trim(), "the helper file is removed once the command is over");
    }

    [TestMethod]
    public void InRealShell_ATokenThatStartsWithADash_IsStillPrintedAsData()
    {
        if (Bash() is null) Assert.Inconclusive("bash is not available.");
        var (exit, output, err) = Run("-s", JitGitCredential.Wrap("\"$GIT_ASKPASS\" 'Password for x: '", new JitGitSecret("token", "-nabc12345678")));
        Assert.AreEqual(0, exit, err);
        Assert.AreEqual("-nabc12345678", output.Trim());
    }

    [TestMethod]
    public void InRealShell_TheCommandsOwnExitStatusIsKept_AndTheHelperIsRemovedEvenWhenItFails()
    {
        if (Bash() is null) Assert.Inconclusive("bash is not available.");
        Assert.AreEqual(0, Run("-s", JitGitCredential.Wrap("true", Secret)).Exit);
        var (exit, output, _) = Run("-s", JitGitCredential.Wrap("echo \"$GIT_ASKPASS\"; exit 7", Secret));
        Assert.AreEqual(7, exit);
        var path = output.Trim();
        Assert.AreEqual("no", Run("-s", $"[ -e '{path}' ] && echo yes || echo no").Out.Trim());
    }

    // ── choosing the credential ──────────────────────────────────────────────

    private sealed class MemoryCredentials : ICredentialResourceStore
    {
        public Dictionary<Guid, CredentialResource> All { get; } = [];
        public int Count => All.Count;
        public Task<List<CredentialResource>> GetAllAsync() => Task.FromResult(All.Values.ToList());
        public Task<List<CredentialResource>> GetByProjectAsync(string projectId) => Task.FromResult(All.Values.Where(c => c.ProjectId == projectId).ToList());
        public Task<CredentialResource?> GetByIdAsync(Guid id) => Task.FromResult(All.GetValueOrDefault(id));
        public Task<CredentialResource?> GetByNameAsync(string name) => Task.FromResult(All.Values.FirstOrDefault(c => c.Name == name));
        public Task SaveAsync(CredentialResource r) { All[r.Id] = r; return Task.CompletedTask; }
        public Task DeleteAsync(Guid id) { All.Remove(id); return Task.CompletedTask; }
    }

    private static CredentialResource Cred(string name = "GitHub PAT", string value = Token, string? project = null, string? host = null) =>
        new() { Name = name, Value = value, ProjectId = project, HostPattern = host };

    [TestMethod]
    public async Task ASavedCredential_IsResolvedForAnHttpsAddress()
    {
        var store = new MemoryCredentials(); var c = Cred(); await store.SaveAsync(c);
        var r = await JitCredentialResolver.ResolveAsync(c.Id, "https://github.com/o/r.git", store, "p1");
        Assert.IsNull(r.Error);
        Assert.AreEqual(Token, r.Secret!.Token);
        Assert.AreEqual("token", r.Secret.Username);
    }

    [TestMethod]
    public async Task EveryWayItCannotBeUsed_IsAPlainMessage_NeverTheToken()
    {
        var store = new MemoryCredentials();
        var c = Cred(); var bad = Cred("Bad", "bad'token-value-1234"); var scoped = Cred("Other", project: "p2");
        foreach (var x in new[] { c, bad, scoped }) await store.SaveAsync(x);

        var ssh = await JitCredentialResolver.ResolveAsync(c.Id, "git@github.com:o/r.git", store, "p1");
        StringAssert.Contains(ssh.Error!, "https");

        var gone = await JitCredentialResolver.ResolveAsync(Guid.NewGuid(), "https://github.com/o/r.git", store, "p1");
        StringAssert.Contains(gone.Error!, "no longer exists");

        var odd = await JitCredentialResolver.ResolveAsync(bad.Id, "https://github.com/o/r.git", store, "p1");
        StringAssert.Contains(odd.Error!, "characters");
        Assert.IsFalse(odd.Error!.Contains("bad'token"));

        var other = await JitCredentialResolver.ResolveAsync(scoped.Id, "https://github.com/o/r.git", store, "p1");
        StringAssert.Contains(other.Error!, "no longer exists");

        var none = await JitCredentialResolver.ResolveAsync(c.Id, "https://github.com/o/r.git", null, "p1");
        Assert.IsNotNull(none.Error);
    }

    [TestMethod]
    public async Task TheSettingsDecideWhetherARequestCarriesACredential()
    {
        var store = new MemoryCredentials(); var c = Cred(); await store.SaveAsync(c);
        var baseReq = Req();

        var without = await JitCredentialResolver.ApplyAsync(baseReq, new JitDeployConfig { RepoUrl = baseReq.RepoUrl }, store, "p1");
        Assert.IsNull(without.Error);
        Assert.IsNull(without.Request!.GitCredential);

        var with = await JitCredentialResolver.ApplyAsync(baseReq with { RepoUrl = "https://github.com/o/r.git" },
            new JitDeployConfig { RepoUrl = "https://github.com/o/r.git", CredentialResourceId = c.Id }, store, "p1");
        Assert.AreEqual(Token, with.Request!.GitCredential!.Token);

        var broken = await JitCredentialResolver.ApplyAsync(baseReq with { RepoUrl = "https://github.com/o/r.git" },
            new JitDeployConfig { RepoUrl = "https://github.com/o/r.git", CredentialResourceId = Guid.NewGuid() }, store, "p1");
        Assert.IsNull(broken.Request);
        Assert.IsNotNull(broken.Error);
    }

    // ── the settings rules ───────────────────────────────────────────────────

    [TestMethod]
    public void ACredentialAndADeployKey_CannotBothBeChosen()
    {
        var p = JitDeployConfigValidator.Validate(new JitDeployConfig { Enabled = true, RepoUrl = "https://github.com/o/r.git", Branch = "main", UseDeployKey = true, CredentialResourceId = Guid.NewGuid() });
        Assert.IsTrue(p.Any(x => x.Field == "credentialResourceId" && x.Message.Contains("either")));
    }

    [TestMethod]
    public void ACredentialNeedsAnHttpsAddress()
    {
        var bad = JitDeployConfigValidator.Validate(new JitDeployConfig { Enabled = true, RepoUrl = "git@github.com:o/r.git", Branch = "main", CredentialResourceId = Guid.NewGuid() });
        Assert.IsTrue(bad.Any(x => x.Field == "credentialResourceId" && x.Message.Contains("https")));
        var ok = JitDeployConfigValidator.Validate(new JitDeployConfig { Enabled = true, RepoUrl = "https://github.com/o/r.git", Branch = "main", CredentialResourceId = Guid.NewGuid() });
        Assert.IsFalse(ok.Any(x => x.Field == "credentialResourceId"));
    }

    // ── the whole deploy ─────────────────────────────────────────────────────

    private static readonly JitTarget Target = new("h", "u", "/k", "/home/deploy/shop");

    private static JitDeployRequest Req(JitGitSecret? secret = null, string? composeFile = null) => new()
    {
        Target = Target, Project = "shop", RepoUrl = "https://github.com/o/r.git", Branch = "master", GitCredential = secret, ComposeFile = composeFile,
        Services = [new JitService("api", "API_TAG", "shop/api"), new JitService("web", "WEB_TAG", "shop/web")],
    };

    private static async Task<(JitDeployResult Result, FakeRemote Remote, List<string> Lines)> Deploy(JitDeployRequest req, Action<FakeRemote>? setup = null)
    {
        var remote = new FakeRemote(); setup?.Invoke(remote);
        var lines = new List<string>();
        var r = await new JitDeployer(remote, new FakeClock(), new JitDeployLock()).DeployAsync(req, l => { lock (lines) lines.Add(l); return Task.CompletedTask; });
        return (r, remote, lines);
    }

    [TestMethod]
    public async Task OnlyTheCommandsThatReachTheGitHost_CarryTheCredential()
    {
        var (r, remote, _) = await Deploy(Req(Secret));
        Assert.AreEqual(JitOutcome.Deployed, r.Outcome, r.Message);

        var carrying = remote.Commands.Where(c => c.Contains(Token)).ToList();
        Assert.AreEqual(2, carrying.Count, "the access check and the sync, nothing else");
        Assert.IsTrue(carrying.Any(c => c.Contains("git ls-remote")));
        Assert.IsTrue(carrying.Any(c => c.Contains("git clone") || c.Contains("fetch --prune")));
        Assert.IsFalse(remote.StateJson?.Contains(Token) ?? false, "never in the saved state on the server");
        var remotes = remote.Commands.SelectMany(c => System.Text.RegularExpressions.Regex.Matches(c, "remote set-url origin '([^']+)'").Select(m => m.Groups[1].Value)).ToList();
        Assert.IsTrue(remotes.Count > 0 || remote.Commands.Any(c => c.Contains("git clone")));
        Assert.IsTrue(remotes.All(u => u == "https://github.com/o/r.git"), "the saved remote is always the clean address");
    }

    [TestMethod]
    public async Task TheTokenIsScrubbed_EvenWhenGitOrTheServerEchoesIt_AndEvenIfItLooksLikeNothingKnown()
    {
        var (r, _, lines) = await Deploy(Req(Secret), f =>
        {
            f.SyncOk = false;
            f.SyncFailureLines = [$"fatal: Authentication failed for 'https://github.com/o/r.git/' (used {Token})"];
        });
        Assert.AreEqual(JitOutcome.FailedBeforeChange, r.Outcome);
        var everything = r.Message + "\n" + string.Join("\n", lines);
        Assert.IsFalse(everything.Contains(Token), everything);
        StringAssert.Contains(everything, "***");
    }

    [TestMethod]
    public async Task AnUnusableToken_IsRefusedBeforeAnythingIsSent()
    {
        var (r, remote, _) = await Deploy(Req(new JitGitSecret("token", "bad'token-value-1234")));
        Assert.AreEqual(JitOutcome.FailedBeforeChange, r.Outcome);
        Assert.AreEqual(0, remote.Commands.Count);
        Assert.IsFalse(r.Message.Contains("bad'token"));
    }

    [TestMethod]
    public async Task ACredentialAndADeployKeyTogether_AreRefused()
    {
        var (r, remote, _) = await Deploy(Req(Secret) with { DeployKeyName = "deploy_shop" });
        Assert.AreEqual(JitOutcome.FailedBeforeChange, r.Outcome);
        StringAssert.Contains(r.Message, "either");
        Assert.AreEqual(0, remote.Commands.Count);
    }

    // ── the list for the dropdown ────────────────────────────────────────────

    private static int Status(IResult r) => ((IStatusCodeHttpResult)r).StatusCode ?? 200;
    private static string Json(IResult r) => JsonSerializer.Serialize(((IValueHttpResult)r).Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    [TestMethod]
    public async Task TheList_ShowsNamesAndHosts_NeverTheValues_AndOnlyThoseThisProjectMayUse()
    {
        var store = new MemoryCredentials();
        await store.SaveAsync(Cred("Everywhere", host: "github.com"));
        await store.SaveAsync(Cred("Only here", project: "p1"));
        await store.SaveAsync(Cred("Somewhere else", project: "p2"));

        var json = Json(await JitCredentialRoutes.HandleListAsync("p1", store));
        StringAssert.Contains(json, "Everywhere");
        StringAssert.Contains(json, "Only here");
        Assert.IsFalse(json.Contains("Somewhere else"));
        Assert.IsFalse(json.Contains(Token), "values are never sent to the screen");
        StringAssert.Contains(json, "github.com");
    }

    [TestMethod]
    public async Task TheListIsEmpty_WhenThereIsNoStore() =>
        StringAssert.Contains(Json(await JitCredentialRoutes.HandleListAsync("p1", null)), "[]");

    // ── the checks use it too ────────────────────────────────────────────────

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

    private sealed class NoConfigs : IJitConfigStore
    {
        public Task<JitDeployConfig?> GetAsync(string id) => Task.FromResult<JitDeployConfig?>(null);
        public Task SaveAsync(string id, JitDeployConfig c) => Task.CompletedTask;
        public Task DeleteAsync(string id) => Task.CompletedTask;
    }

    private sealed class FakeSsh : ISshRunner
    {
        public int Exit { get; set; }
        public string[] Output { get; set; } = [];
        public List<string> Commands { get; } = [];
        public async Task<int> RunAsync(string host, string username, string keyPath, string command, Func<string, Task>? onOutput = null, Func<string, Task>? onStderr = null, CancellationToken ct = default)
        {
            Commands.Add(command);
            if (onOutput is not null) foreach (var l in Output) await onOutput(l);
            return Exit;
        }
    }

    private static async Task<MemoryProjects> Store()
    {
        var s = new MemoryProjects();
        await s.SaveAsync(new ProjectConfig { Id = "p1", Name = "Shop", Server = new ServerConfig { Host = "h", Username = "u", SshKeyPath = "/k", RemoteWorkingDir = "/home/u/shop" } });
        return s;
    }

    [TestMethod]
    public async Task TheAccessCheck_CanUseASavedCredential_WithoutTheTokenInTheAnswer()
    {
        var creds = new MemoryCredentials(); var c = Cred(); await creds.SaveAsync(c);
        var ssh = new FakeSsh { Exit = 1, Output = ["[preflight] Cannot reach the git repository."] };
        var r = await JitDeployKeyRoutes.HandleGitAccessAsync("p1", new GitAccessRequest("https://github.com/o/r.git", false, c.Id), await Store(), new NoConfigs(), ssh, creds);

        Assert.AreEqual(200, Status(r));
        Assert.IsFalse(Json(r).Contains(Token));
        StringAssert.Contains(ssh.Commands.Single(), "GIT_ASKPASS=");
        StringAssert.Contains(ssh.Commands.Single(), Token);
    }

    [TestMethod]
    public async Task TheAccessCheck_RefusesACredentialForAnSshAddress_Plainly()
    {
        var creds = new MemoryCredentials(); var c = Cred(); await creds.SaveAsync(c);
        var ssh = new FakeSsh();
        var r = await JitDeployKeyRoutes.HandleGitAccessAsync("p1", new GitAccessRequest("git@github.com:o/r.git", false, c.Id), await Store(), new NoConfigs(), ssh, creds);
        Assert.AreEqual(400, Status(r));
        StringAssert.Contains(Json(r), "https");
        Assert.AreEqual(0, ssh.Commands.Count);
    }

    [TestMethod]
    public async Task TheBranchList_CanUseASavedCredential()
    {
        var creds = new MemoryCredentials(); var c = Cred(); await creds.SaveAsync(c);
        var ssh = new FakeSsh { Output = ["ref: refs/heads/main\tHEAD", "2222222222222222222222222222222222222222\trefs/heads/main"] };
        var r = await JitHintRoutes.HandleBranchesAsync("p1", new BranchesRequest("https://github.com/o/r.git", c.Id), await Store(), ssh, creds);

        StringAssert.Contains(Json(r), "\"defaultBranch\":\"main\"");
        Assert.IsFalse(Json(r).Contains(Token));
        StringAssert.Contains(ssh.Commands.Single(), "GIT_ASKPASS=");
    }
}
