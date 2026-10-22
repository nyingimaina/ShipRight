using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Identity;
using ShipRight.Modules.Projects;
using ShipRight.Modules.RemoteHost;
using ShipRight.Tests.Modules.Identity;

namespace ShipRight.Tests.Modules.RemoteHost;

// The managed-key generate/authorize flow: the status the settings screen reads (R1) and the
// "generate first" guard (R2), plus the saved-key-file provider path.
[TestClass]
public class SshKeyRouterTests
{
    private static InMemoryCloudFactory _factory = null!;

    [ClassInitialize]
    public static void Start(TestContext _) => _factory = new InMemoryCloudFactory();

    [ClassCleanup]
    public static void Stop() => _factory.Dispose();

    private static string NewEmail(string prefix = "sshk") => $"{prefix}-{Guid.NewGuid():N}@example.test";

    private static async Task<ApiClient> SignedIn()
    {
        var user = _factory.Seed(NewEmail());
        var client = _factory.NewClient();
        Assert.AreEqual(HttpStatusCode.OK, (await client.Login(user.Email)).Status);
        return client;
    }

    private static void ReturnServer(string id, bool managed, bool authorized)
    {
        var json = "{\"id\":\"" + id + "\",\"name\":\"prod\",\"host\":\"10.0.0.5\",\"username\":\"ubuntu\"," +
                   "\"managedSshKey\":" + (managed ? "true" : "false") + "," +
                   "\"managedSshKeyAuthorized\":" + (authorized ? "true" : "false") + "}";
        _factory.Db.ReturnOneBlankRow = true;
        _factory.Db.CustomizeBlankRow = row => row.GetType().GetProperty("Data")!.SetValue(row, json);
    }

    private static void ClearReads()
    {
        _factory.Db.ReturnOneBlankRow = false;
        _factory.Db.CustomizeBlankRow = null;
    }

    // ── R1: the screen can see whether a managed key exists and is active ─────

    [TestMethod]
    public async Task Status_WithNoManagedKey_ReportsNothingExists_AndNothingIsActive()
    {
        // R1
        var client = await SignedIn();
        var id = Guid.NewGuid().ToString("N");
        ReturnServer(id, managed: false, authorized: false);
        try
        {
            var status = await client.Get($"/api/servers/{id}/ssh-key/status");

            Assert.AreEqual(HttpStatusCode.OK, status.Status, status.Body);
            Assert.IsFalse(status.Json.GetProperty("exists").GetBoolean());
            Assert.IsFalse(status.Json.GetProperty("managedSshKey").GetBoolean());
            Assert.IsFalse(status.Json.GetProperty("authorized").GetBoolean());
        }
        finally { ClearReads(); }
    }

    [TestMethod]
    public async Task Status_ReportsThatAManagedKeyIsActiveForDeployments()
    {
        // R1
        var client = await SignedIn();
        var id = Guid.NewGuid().ToString("N");
        ReturnServer(id, managed: true, authorized: true);
        try
        {
            var status = await client.Get($"/api/servers/{id}/ssh-key/status");

            Assert.AreEqual(HttpStatusCode.OK, status.Status, status.Body);
            Assert.IsTrue(status.Json.GetProperty("managedSshKey").GetBoolean());
            Assert.IsTrue(status.Json.GetProperty("authorized").GetBoolean());
        }
        finally { ClearReads(); }
    }

    [TestMethod]
    public void ServerConfig_CarriesTheAuthorizationFlag_AndDefaultsToNotAuthorized()
    {
        // R1, R2: a server that has never been authorized is not marked as authorized.
        var fresh = new ServerConfig { Host = "h", Username = "u" };
        Assert.IsFalse(fresh.ManagedSshKeyAuthorized);

        var regenerated = fresh with { ManagedSshKey = true, ManagedSshKeyAuthorized = false };
        Assert.IsFalse(regenerated.ManagedSshKeyAuthorized, "regenerating drops the old authorization");
    }

    // ── R2: authorization only after a key exists, and signed-in ─────────────

    [TestMethod]
    public async Task Authorize_WithoutSignIn_IsRejected()
    {
        // R8-style sign-in guard, applied to R2
        var result = await _factory.NewClient().Post($"/api/servers/{Guid.NewGuid():N}/ssh-key/authorize", new { password = "x" });

        Assert.AreEqual(HttpStatusCode.Unauthorized, result.Status);
    }

    [TestMethod]
    public async Task Authorize_BeforeAKeyExists_TellsTheUserToGenerateOneFirst()
    {
        // R2 edge case: authorizing is offered only after a managed key exists.
        var client = await SignedIn();
        var id = Guid.NewGuid().ToString("N");
        ReturnServer(id, managed: false, authorized: false);
        try
        {
            var result = await client.Post($"/api/servers/{id}/ssh-key/authorize", new { password = "secret" });

            Assert.AreEqual(HttpStatusCode.BadRequest, result.Status, result.Body);
            StringAssert.Contains(result.Text("message")!, "Generate an SSH key first");
        }
        finally { ClearReads(); }
    }

    // ── R2: the saved-key-file provider path ─────────────────────────────────

    [TestMethod]
    public async Task AuthorizeKeyWithFile_WithNoContent_IsRejectedClearly()
    {
        // R2
        var provider = new LinuxSshProvider();
        var config = new RemoteHostConfig("10.0.0.5", 22, "ubuntu");

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => provider.AuthorizeKeyWithFileAsync(config, "", "ssh-ed25519 AAAA test"));
    }

    [TestMethod]
    public async Task AuthorizeKeyWithFile_WithAKeyTheServerRejects_AsksForAnUnprotectedKeyFile()
    {
        // R2 edge case: a passphrase-protected or unreadable .pem is refused with a message, in memory only.
        var provider = new LinuxSshProvider();
        var config = new RemoteHostConfig("10.0.0.5", 22, "ubuntu");

        var ex = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => provider.AuthorizeKeyWithFileAsync(config, "not-a-private-key", "ssh-ed25519 AAAA test"));

        StringAssert.Contains(ex.Message, "passphrase");
    }

    // ── R1: the private key never reaches the browser (QA-1) ─────────────────

    private const string PrivateKeyMaterial = "-----BEGIN OPENSSH PRIVATE KEY-----\nPRIVATE-BODY-MUST-NOT-LEAK\n-----END OPENSSH PRIVATE KEY-----\n";
    private const string PublicKeyLine = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAITESTSHIPRIGHTPUBLICKEY shipright@test";

    [TestMethod]
    public async Task Status_WithAManagedKey_NeverReturnsThePrivateKey()
    {
        // QA-1, R1 acceptance: the private key is never returned to the browser — only the public key.
        var dataDir = NewTempDataDir();
        var id = Guid.NewGuid().ToString("N");
        WriteManagedKeyPair(dataDir, id);
        var user = _factory.Seed(NewEmail());
        using var derived = _factory.WithWebHostBuilder(b =>
            b.ConfigureTestServices(s =>
            {
                s.RemoveAll<SshKeyStore>();
                s.AddSingleton(new SshKeyStore(dataDir));
            }));
        var client = new ApiClient(derived.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true }));
        try
        {
            Assert.AreEqual(HttpStatusCode.OK, (await client.Login(user.Email)).Status);
            ReturnServer(id, managed: true, authorized: true);

            var status = await client.Get($"/api/servers/{id}/ssh-key/status");

            Assert.AreEqual(HttpStatusCode.OK, status.Status, status.Body);
            Assert.AreEqual(PublicKeyLine, status.Text("publicKey"));
            Assert.IsFalse(status.Body.Contains("PRIVATE-BODY-MUST-NOT-LEAK"), status.Body);
            Assert.IsFalse(status.Body.Contains("PRIVATE KEY"), status.Body);
            var properties = status.Json.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray();
            CollectionAssert.AreEqual(new[] { "authorized", "exists", "managedSshKey", "publicKey" }, properties);
        }
        finally
        {
            ClearReads();
            TryDelete(dataDir);
        }
    }

    [TestMethod]
    public async Task Generate_NeverReturnsThePrivateKey()
    {
        // QA-1, R1 acceptance: even when the key is created, only the public key travels to the browser.
        var dataDir = NewTempDataDir();
        var id = Guid.NewGuid().ToString("N");
        var user = _factory.Seed(NewEmail());
        using var derived = _factory.WithWebHostBuilder(b =>
            b.ConfigureTestServices(s =>
            {
                s.RemoveAll<SshKeyStore>();
                s.AddSingleton(new SshKeyStore(dataDir));
            }));
        var client = new ApiClient(derived.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true }));
        try
        {
            Assert.AreEqual(HttpStatusCode.OK, (await client.Login(user.Email)).Status);
            ReturnServer(id, managed: false, authorized: false);

            var result = await client.Post($"/api/servers/{id}/ssh-key/generate");

            Assert.AreEqual(HttpStatusCode.OK, result.Status, result.Body);
            StringAssert.StartsWith(result.Text("publicKey"), "ssh-ed25519 ");
            Assert.IsFalse(result.Body.Contains("PRIVATE KEY"), result.Body);
            var properties = result.Json.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray();
            CollectionAssert.AreEqual(new[] { "authorized", "keyPath", "publicKey" }, properties);
        }
        finally
        {
            ClearReads();
            TryDelete(dataDir);
        }
    }

    // ── R2: a failed authorization leaves no half-authorized state (QA-2) ────

    [TestMethod]
    public async Task Authorize_WhenTheServerCannotBeReached_LeavesTheServerNotAuthorized()
    {
        // QA-2, R2 edge case: a failed authorize must not mark the key authorized or touch server settings.
        var dataDir = NewTempDataDir();
        var id = Guid.NewGuid().ToString("N");
        WriteManagedKeyPair(dataDir, id);
        var user = _factory.Seed(NewEmail());
        using var derived = _factory.WithWebHostBuilder(b =>
            b.ConfigureTestServices(s =>
            {
                s.RemoveAll<SshKeyStore>();
                s.AddSingleton(new SshKeyStore(dataDir));
                s.RemoveAll<IRemoteHostProvider>();
                s.AddSingleton<IRemoteHostProvider>(new FailingRemoteHost());
            }));
        var client = new ApiClient(derived.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true }));
        try
        {
            Assert.AreEqual(HttpStatusCode.OK, (await client.Login(user.Email)).Status);
            ReturnServer(id, managed: true, authorized: false);
            _factory.Db.Statements.Clear();

            var result = await client.Post($"/api/servers/{id}/ssh-key/authorize", new { password = "hunter2-secret" });

            Assert.AreEqual(HttpStatusCode.BadRequest, result.Status, result.Body);
            Assert.IsFalse(result.Body.Contains("hunter2-secret"), result.Body);
            Assert.IsFalse(
                _factory.Db.Statements.Any(s => s.Sql.Contains("UPDATE Server") || s.Sql.Contains("INSERT INTO Server")),
                string.Join("\n", _factory.Db.Statements.Select(s => s.Sql)));
        }
        finally
        {
            ClearReads();
            TryDelete(dataDir);
        }
    }

    private sealed class FailingRemoteHost : IRemoteHostProvider
    {
        public string ProviderType => "failing";
        public Task AuthorizeKeyAsync(RemoteHostConfig config, string password, string publicKey, CancellationToken ct = default) =>
            throw new InvalidOperationException("The server could not be reached.");
        public Task AuthorizeKeyWithFileAsync(RemoteHostConfig config, string privateKeyContent, string publicKey, CancellationToken ct = default) =>
            throw new InvalidOperationException("The server could not be reached.");
        public Task<bool> IsKeyAuthorizedAsync(RemoteHostConfig config, string keyPath, CancellationToken ct = default) =>
            Task.FromResult(false);
    }

    private static string NewTempDataDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "shipright-sshk-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteManagedKeyPair(string dataDir, string serverId)
    {
        var sshDir = Path.Combine(dataDir, "ssh");
        Directory.CreateDirectory(sshDir);
        File.WriteAllText(Path.Combine(sshDir, $"server-{serverId}.pem"), PrivateKeyMaterial);
        File.WriteAllText(Path.Combine(sshDir, $"server-{serverId}.pub"), PublicKeyLine);
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* temp folder, best effort */ }
    }
}
