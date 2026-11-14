using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.ConnectionTest;
using ShipRight.Modules.Projects;
using ShipRight.Modules.Servers;

namespace ShipRight.Tests.Modules.ConnectionTest;

/// <summary>
/// Showing a server's identity (host key fingerprint) before ShipRight trusts it, so the user can compare it with the
/// one the server reports about itself. Trusting is only possible for the fingerprint just shown.
/// </summary>
[TestClass]
public class HostKeyTests
{
    private static readonly byte[] Abc = Encoding.ASCII.GetBytes("abc");

    // ── fingerprints ─────────────────────────────────────────────────────────

    [TestMethod]
    public void Sha256_MatchesTheFormatOpenSshPrints_WithoutPadding() =>
        Assert.AreEqual("SHA256:ungWv48Bz+pBQUDeXa4iI7ADYaOWF3qctBD/YfIAFa0", HostKeyFingerprint.Sha256(Abc));

    [TestMethod]
    public void Md5_IsLowerCaseColonSeparated_LikeTheFormatTheSshRunnerStores() =>
        Assert.AreEqual("90:01:50:98:3c:d2:4f:b0:d6:96:3f:7d:28:e1:7f:72", HostKeyFingerprint.Md5(Abc));

    [DataTestMethod]
    [DataRow("ssh-ed25519", "ssh_host_ed25519_key.pub")]
    [DataRow("ssh-rsa", "ssh_host_rsa_key.pub")]
    [DataRow("rsa-sha2-512", "ssh_host_rsa_key.pub")]
    [DataRow("ecdsa-sha2-nistp256", "ssh_host_ecdsa_key.pub")]
    [DataRow("something-new", "ssh_host_*_key.pub")]
    public void VerifyCommand_PointsAtTheRightHostKeyFile(string algorithm, string file)
    {
        var c = HostKeyFingerprint.VerifyCommand(algorithm);
        StringAssert.StartsWith(c, "ssh-keygen -lf /etc/ssh/");
        StringAssert.Contains(c, file);
        Assert.IsFalse(c.Contains("sudo") || c.Contains(";") || c.Contains("&&"), "a single, read-only command");
    }

    // ── endpoints ────────────────────────────────────────────────────────────

    private sealed class MemoryServers : IServerStore
    {
        public Dictionary<string, ServerConfig> All { get; } = [];
        public Task<List<ServerConfig>> GetAllAsync() => Task.FromResult(All.Values.ToList());
        public Task<ServerConfig?> GetByIdAsync(string id) => Task.FromResult(All.GetValueOrDefault(id));
        public Task SaveAsync(ServerConfig s) { All[s.Id] = s; return Task.CompletedTask; }
        public Task DeleteAsync(string id) { All.Remove(id); return Task.CompletedTask; }
    }

    private sealed class FakeScanner : IHostKeyScanner
    {
        public byte[] Blob { get; set; } = Abc;
        public string Algorithm { get; set; } = "ssh-ed25519";
        public Exception? Throw { get; set; }
        public List<string> Hosts { get; } = [];
        public Task<HostKeyScan> ScanAsync(string host, int port, CancellationToken ct)
        {
            Hosts.Add(host);
            if (Throw is not null) throw Throw;
            return Task.FromResult(new HostKeyScan(Algorithm, Blob));
        }
    }

    private sealed class FakeKnown : IKnownHosts
    {
        public Dictionary<string, string> Entries { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string? Stored(string host) => Entries.GetValueOrDefault(host);
        public void Trust(string host, string fingerprint) => Entries[host] = fingerprint;
    }

    private static async Task<MemoryServers> WithServer()
    {
        var s = new MemoryServers();
        await s.SaveAsync(new ServerConfig { Id = "s1", Name = "box", Host = "box.example", Username = "u" });
        return s;
    }

    private static int Status(IResult r) => ((IStatusCodeHttpResult)r).StatusCode ?? 200;
    private static string Json(IResult r) =>
        JsonSerializer.Serialize(((IValueHttpResult)r).Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    [TestMethod]
    public async Task Scan_UnknownServer_Is404() =>
        Assert.AreEqual(404, Status(await HostKeyRoutes.HandleScanAsync("x", new MemoryServers(), new FakeScanner(), new FakeKnown(), default)));

    [TestMethod]
    public async Task Scan_ANewServer_ShowsTheFingerprint_AndHowToCompareIt()
    {
        var known = new FakeKnown();
        var r = await HostKeyRoutes.HandleScanAsync("s1", await WithServer(), new FakeScanner(), known, default);

        Assert.AreEqual(200, Status(r));
        var json = Json(r);
        StringAssert.Contains(json, "\"state\":\"new\"");
        StringAssert.Contains(json, "SHA256:ungWv48Bz");   // the rest holds a "+", which JSON escapes
        StringAssert.Contains(json, "ssh_host_ed25519_key.pub");
        Assert.AreEqual(0, known.Entries.Count, "scanning alone never trusts anything");
    }

    [TestMethod]
    public async Task Scan_AKnownMatchingServer_IsTrusted()
    {
        var known = new FakeKnown();
        known.Entries["box.example"] = HostKeyFingerprint.Md5(Abc);
        StringAssert.Contains(Json(await HostKeyRoutes.HandleScanAsync("s1", await WithServer(), new FakeScanner(), known, default)), "\"state\":\"trusted\"");
    }

    [TestMethod]
    public async Task Scan_AServerWhoseKeyChanged_IsFlaggedLoudly()
    {
        var known = new FakeKnown();
        known.Entries["box.example"] = "aa:bb";
        var json = Json(await HostKeyRoutes.HandleScanAsync("s1", await WithServer(), new FakeScanner(), known, default));
        StringAssert.Contains(json, "\"state\":\"changed\"");
        StringAssert.Contains(json, "different");
    }

    [TestMethod]
    public async Task Scan_WhenTheServerCannotBeReached_ExplainsInPlainWords()
    {
        var r = await HostKeyRoutes.HandleScanAsync("s1", await WithServer(), new FakeScanner { Throw = new IOException("boom") }, new FakeKnown(), default);
        Assert.AreEqual(502, Status(r));
        StringAssert.Contains(Json(r), "could not read");
    }

    private static string FreshSha => HostKeyFingerprint.Sha256(Abc);

    [TestMethod]
    public async Task Trust_StoresTheFingerprintThatWasJustShown()
    {
        var known = new FakeKnown();
        var r = await HostKeyRoutes.HandleTrustAsync("s1", new TrustHostKeyRequest(FreshSha, false), await WithServer(), new FakeScanner(), known, default);

        Assert.AreEqual(200, Status(r));
        Assert.AreEqual(HostKeyFingerprint.Md5(Abc), known.Entries["box.example"]);
    }

    [TestMethod]
    public async Task Trust_AFingerprintThatNoLongerMatchesTheServer_IsRefused()
    {
        var known = new FakeKnown();
        var r = await HostKeyRoutes.HandleTrustAsync("s1", new TrustHostKeyRequest("SHA256:old", false), await WithServer(), new FakeScanner(), known, default);

        Assert.AreEqual(409, Status(r));
        Assert.AreEqual(0, known.Entries.Count);
    }

    [TestMethod]
    public async Task Trust_ReplacingADifferentKnownKey_NeedsAnExplicitAcknowledgement()
    {
        var known = new FakeKnown();
        known.Entries["box.example"] = "aa:bb";
        var servers = await WithServer();

        var refused = await HostKeyRoutes.HandleTrustAsync("s1", new TrustHostKeyRequest(FreshSha, false), servers, new FakeScanner(), known, default);
        Assert.AreEqual(400, Status(refused));
        Assert.AreEqual("aa:bb", known.Entries["box.example"]);

        var ok = await HostKeyRoutes.HandleTrustAsync("s1", new TrustHostKeyRequest(FreshSha, true), servers, new FakeScanner(), known, default);
        Assert.AreEqual(200, Status(ok));
        Assert.AreEqual(HostKeyFingerprint.Md5(Abc), known.Entries["box.example"]);
    }

    [TestMethod]
    public async Task Trust_UnknownServer_Is404_AndScannerFailureIs502()
    {
        Assert.AreEqual(404, Status(await HostKeyRoutes.HandleTrustAsync("x", new TrustHostKeyRequest(FreshSha, false), new MemoryServers(), new FakeScanner(), new FakeKnown(), default)));
        Assert.AreEqual(502, Status(await HostKeyRoutes.HandleTrustAsync("s1", new TrustHostKeyRequest(FreshSha, false), await WithServer(), new FakeScanner { Throw = new IOException("x") }, new FakeKnown(), default)));
    }

    [TestMethod]
    public async Task Trust_NeverTakesTheHostFromTheRequest()
    {
        var scanner = new FakeScanner();
        await HostKeyRoutes.HandleTrustAsync("s1", new TrustHostKeyRequest(FreshSha, false), await WithServer(), scanner, new FakeKnown(), default);
        CollectionAssert.AreEqual(new[] { "box.example" }, scanner.Hosts);
    }
}
