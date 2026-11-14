using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Renci.SshNet.Common;
using ShipRight.Modules.ConnectionTest;
using ShipRight.Modules.Projects;
using ShipRight.Modules.Servers;
using ShipRight.Shared.SshRunner;

namespace ShipRight.Tests.Modules.ConnectionTest;

[TestClass]
public class ConnectionTesterTests
{
    private sealed class FakeSsh : ISshRunner
    {
        public List<(string Host, string User, string Key, string Command)> Calls { get; } = [];
        public string[] Output { get; set; } = ["user:ubuntu", "sudo:ok", "git:ok", "docker:installed", "dockeruse:ok", "compose:ok", "disk:50000", "dir:ok", "pkg:apt"];
        public int Exit { get; set; }
        public Exception? Throw { get; set; }

        public async Task<int> RunAsync(string host, string username, string keyPath, string command,
            Func<string, Task>? onOutput = null, Func<string, Task>? onStderr = null, CancellationToken ct = default)
        {
            Calls.Add((host, username, keyPath, command));
            ct.ThrowIfCancellationRequested();
            if (Throw is not null) throw Throw;
            if (onOutput is not null) foreach (var l in Output) await onOutput(l);
            return Exit;
        }
    }

    private sealed class FakeTcp : ITcpProbe
    {
        public TcpProbeResult Result { get; set; } = TcpProbeResult.Success;
        public List<(string Host, int Port)> Calls { get; } = [];
        public Task<TcpProbeResult> ProbeAsync(string host, int port, TimeSpan timeout, CancellationToken ct)
        {
            Calls.Add((host, port));
            return Task.FromResult(Result);
        }
    }

    private static ServerConfig Server(string host = "box.tail1.ts.net", string key = "/keys/k.pem", string dir = "/home/ubuntu/shop") =>
        new() { Id = "s1", Name = "box", Host = host, Username = "ubuntu", SshKeyPath = key, RemoteWorkingDir = dir };

    private static (ConnectionTester T, FakeSsh Ssh, FakeTcp Tcp) Rig() { var s = new FakeSsh(); var t = new FakeTcp(); return (new ConnectionTester(s, t), s, t); }

    private static CheckItem Item(ConnectionTestResult r, string id) => r.Items.Single(i => i.Id == id);

    [TestMethod]
    public async Task AHealthyServer_IsReady_WithEveryCheckInOrder()
    {
        var (t, _, _) = Rig();
        var r = await t.TestAsync(Server());

        Assert.IsTrue(r.Ready);
        CollectionAssert.AreEqual(new[] { "reach", "login", "sudo", "docker", "compose", "git", "disk", "folder" }, r.Items.Select(i => i.Id).ToArray());
        Assert.IsTrue(r.Items.All(i => i.Status == CheckStatus.Pass));
        StringAssert.Contains(r.Summary, "ready");
    }

    [TestMethod]
    public async Task ItUsesTheStoredHostUserAndKey_AndPort22_AndSendsOneReadOnlyCommand()
    {
        var (t, ssh, tcp) = Rig();
        await t.TestAsync(Server());

        Assert.AreEqual(1, ssh.Calls.Count);
        Assert.AreEqual(("box.tail1.ts.net", "ubuntu", "/keys/k.pem"), (ssh.Calls[0].Host, ssh.Calls[0].User, ssh.Calls[0].Key));
        StringAssert.StartsWith(ssh.Calls[0].Command, "sh -c ");
        Assert.AreEqual(("box.tail1.ts.net", 22), tcp.Calls.Single());
    }

    [TestMethod]
    public async Task NoHost_FailsReachability_AndTouchesNothing()
    {
        var (t, ssh, tcp) = Rig();
        var r = await t.TestAsync(Server(host: " "));

        Assert.IsFalse(r.Ready);
        Assert.AreEqual(CheckStatus.Fail, Item(r, "reach").Status);
        Assert.AreEqual(0, tcp.Calls.Count);
        Assert.AreEqual(0, ssh.Calls.Count);
        Assert.AreEqual(CheckStatus.Skipped, Item(r, "login").Status);
    }

    [TestMethod]
    public async Task NoKeyYet_FailsLogin_WithoutConnecting_AndPointsAtTheKeyStep()
    {
        var (t, ssh, _) = Rig();
        var r = await t.TestAsync(Server(key: ""));

        Assert.AreEqual(CheckStatus.Pass, Item(r, "reach").Status);
        var login = Item(r, "login");
        Assert.AreEqual(CheckStatus.Fail, login.Status);
        StringAssert.Contains(login.Message, "key");
        Assert.AreEqual(0, ssh.Calls.Count);
        Assert.IsTrue(r.Items.Where(i => i.Id is "sudo" or "docker" or "compose" or "git" or "disk" or "folder").All(i => i.Status == CheckStatus.Skipped));
    }

    [TestMethod]
    public async Task AnUnreachableServer_SkipsEverythingAfter_AndNeverTriesToLogIn()
    {
        var (t, ssh, tcp) = Rig();
        tcp.Result = TcpProbeResult.Failed(TcpFailure.TimedOut);
        var r = await t.TestAsync(Server());

        Assert.IsFalse(r.Ready);
        Assert.AreEqual(CheckStatus.Fail, Item(r, "reach").Status);
        Assert.AreEqual(0, ssh.Calls.Count);
        Assert.IsTrue(r.Items.Where(i => i.Id != "reach").All(i => i.Status == CheckStatus.Skipped));
    }

    [TestMethod]
    public async Task ARefusedKey_FailsLogin_AfterPassingReachability_AndSkipsTheRest()
    {
        var (t, ssh, _) = Rig();
        ssh.Throw = new SshAuthenticationException("Permission denied (publickey).");
        var r = await t.TestAsync(Server());

        Assert.AreEqual(CheckStatus.Pass, Item(r, "reach").Status);
        Assert.AreEqual(CheckStatus.Fail, Item(r, "login").Status);
        StringAssert.Contains(Item(r, "login").Fix!, "authorized_keys");
        Assert.IsTrue(r.Items.Where(i => i.Id is not ("reach" or "login")).All(i => i.Status == CheckStatus.Skipped));
    }

    [TestMethod]
    public async Task AHandshakeFailure_IsAReachProblem_NotALoginProblem()
    {
        var (t, ssh, _) = Rig();
        ssh.Throw = new SshConnectionException("Server response does not contain SSH protocol identification.");
        var r = await t.TestAsync(Server());

        Assert.AreEqual(CheckStatus.Fail, Item(r, "reach").Status);
        Assert.AreEqual(CheckStatus.Skipped, Item(r, "login").Status);
    }

    [TestMethod]
    public async Task Cancellation_IsNotSwallowed()
    {
        var (t, _, _) = Rig();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => t.TestAsync(Server(), cts.Token));
    }

    [TestMethod]
    public async Task AProbeThatRunsButFails_StillCountsAsLoggedIn_AndSaysTheDetailsCouldNotBeRead()
    {
        var (t, ssh, _) = Rig();
        ssh.Exit = 127; ssh.Output = ["sh: 1: weird: not found"];
        var r = await t.TestAsync(Server());

        Assert.AreEqual(CheckStatus.Pass, Item(r, "login").Status);
        Assert.IsFalse(r.Items.Any(i => i.Status == CheckStatus.Pass && i.Id != "reach" && i.Id != "login"));
        Assert.IsTrue(r.Items.Where(i => i.Id is not ("reach" or "login")).All(i => i.Status is CheckStatus.Warn or CheckStatus.Skipped));
    }

    [TestMethod]
    public async Task AFolderNameThatCannotBeChecked_FailsTheFolderCheck_ButTheRestStillRuns()
    {
        var (t, ssh, _) = Rig();
        var r = await t.TestAsync(Server(dir: "relative/path"));

        Assert.AreEqual(CheckStatus.Fail, Item(r, "folder").Status);
        StringAssert.Contains(Item(r, "folder").Message, "absolute");
        Assert.AreEqual(1, ssh.Calls.Count);
        Assert.AreEqual(CheckStatus.Pass, Item(r, "git").Status);
    }

    [TestMethod]
    public async Task AFreshServer_IsReady_WithWarnings_BecauseTheFirstDeployInstallsTheTools()
    {
        var (t, ssh, _) = Rig();
        ssh.Output = ["user:ubuntu", "sudo:ok", "git:missing", "docker:missing", "disk:50000", "dir:creatable", "pkg:apt"];
        var r = await t.TestAsync(Server());

        Assert.IsTrue(r.Ready, "warnings do not block");
        Assert.AreEqual(CheckStatus.Warn, Item(r, "docker").Status);
        StringAssert.Contains(r.Summary, "automatically");
    }

    [TestMethod]
    public async Task AServerThatCannotInstall_IsNotReady_AndTheSummaryNamesTheFirstProblem()
    {
        var (t, ssh, _) = Rig();
        ssh.Output = ["user:ubuntu", "sudo:password", "git:missing", "docker:missing", "disk:50000", "dir:ok", "pkg:apt"];
        var r = await t.TestAsync(Server());

        Assert.IsFalse(r.Ready);
        StringAssert.Contains(r.Summary, "password");
    }

    [TestMethod]
    public async Task TheResult_NeverContainsTheKeyPath()
    {
        var (t, ssh, _) = Rig();
        ssh.Throw = new FileNotFoundException("Could not find file", "/keys/k.pem");
        var r = await t.TestAsync(Server());
        Assert.IsFalse(JsonSerializer.Serialize(r.Items).Contains("/keys/k.pem"));
    }

    // ── the endpoint ─────────────────────────────────────────────────────────

    private sealed class MemoryServers : IServerStore
    {
        public Dictionary<string, ServerConfig> All { get; } = [];
        public Task<List<ServerConfig>> GetAllAsync() => Task.FromResult(All.Values.ToList());
        public Task<ServerConfig?> GetByIdAsync(string id) => Task.FromResult(All.GetValueOrDefault(id));
        public Task SaveAsync(ServerConfig s) { All[s.Id] = s; return Task.CompletedTask; }
        public Task DeleteAsync(string id) { All.Remove(id); return Task.CompletedTask; }
    }

    private static int Status(IResult r) => ((IStatusCodeHttpResult)r).StatusCode ?? 200;
    private static string Json(IResult r) =>
        JsonSerializer.Serialize(((IValueHttpResult)r).Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    [TestMethod]
    public async Task Endpoint_UnknownServer_Is404()
    {
        var r = await ConnectionTestRoutes.HandleAsync("nope", new MemoryServers(), new FakeSsh(), new FakeTcp(), default);
        Assert.AreEqual(404, Status(r));
    }

    [TestMethod]
    public async Task Endpoint_ReturnsReadinessAndLowerCaseStatuses()
    {
        var store = new MemoryServers();
        await store.SaveAsync(Server());
        var r = await ConnectionTestRoutes.HandleAsync("s1", store, new FakeSsh(), new FakeTcp(), default);

        Assert.AreEqual(200, Status(r));
        var json = Json(r);
        StringAssert.Contains(json, "\"ready\":true");
        StringAssert.Contains(json, "\"status\":\"pass\"");
        StringAssert.Contains(json, "\"id\":\"reach\"");
    }

    [TestMethod]
    public async Task Endpoint_AFailingCheck_IsStillA200_WithTheFailureInside()
    {
        var store = new MemoryServers();
        await store.SaveAsync(Server());
        var tcp = new FakeTcp { Result = TcpProbeResult.Failed(TcpFailure.Refused) };
        var r = await ConnectionTestRoutes.HandleAsync("s1", store, new FakeSsh(), tcp, default);

        Assert.AreEqual(200, Status(r));
        StringAssert.Contains(Json(r), "\"ready\":false");
        StringAssert.Contains(Json(r), "\"status\":\"fail\"");
    }

    [TestMethod]
    public async Task Endpoint_AnUnexpectedCrash_BecomesAReadableFailure_NotA500()
    {
        var store = new MemoryServers();
        await store.SaveAsync(Server());
        var ssh = new FakeSsh { Throw = new InvalidOperationException("kaboom") };
        var r = await ConnectionTestRoutes.HandleAsync("s1", store, ssh, new FakeTcp(), default);

        Assert.AreEqual(200, Status(r));
        StringAssert.Contains(Json(r), "kaboom");
    }
}
