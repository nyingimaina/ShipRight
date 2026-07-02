using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.RemoteHost;

namespace ShipRight.Tests.Modules.RemoteHost;

[TestClass]
public class SshKeyStoreTests
{
    private string _tmpDir = null!;

    [TestInitialize]
    public void Setup() =>
        _tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_tmpDir, true); } catch { }
    }

    [TestMethod]
    public async Task GenerateAsync_CreatesPrivateKeyFile()
    {
        var store = new SshKeyStore(_tmpDir);
        await store.GenerateAsync("proj1");
        Assert.IsTrue(File.Exists(Path.Combine(_tmpDir, "ssh", "proj1.pem")));
    }

    [TestMethod]
    public async Task GenerateAsync_CreatesPublicKeyFile()
    {
        var store = new SshKeyStore(_tmpDir);
        await store.GenerateAsync("proj1");
        Assert.IsTrue(File.Exists(Path.Combine(_tmpDir, "ssh", "proj1.pub")));
    }

    [TestMethod]
    public async Task GetPublicKeyAsync_ReturnsEd25519Key()
    {
        var store = new SshKeyStore(_tmpDir);
        await store.GenerateAsync("proj1");
        var pub = await store.GetPublicKeyAsync("proj1");
        StringAssert.StartsWith(pub, "ssh-ed25519 ");
    }

    [TestMethod]
    public async Task ExistsAsync_ReturnsFalseBeforeGenerate()
    {
        var store = new SshKeyStore(_tmpDir);
        Assert.IsFalse(await store.ExistsAsync("proj1"));
    }

    [TestMethod]
    public async Task ExistsAsync_ReturnsTrueAfterGenerate()
    {
        var store = new SshKeyStore(_tmpDir);
        await store.GenerateAsync("proj1");
        Assert.IsTrue(await store.ExistsAsync("proj1"));
    }

    [TestMethod]
    public async Task GetPrivateKeyPath_ReturnsPathThatExists()
    {
        var store = new SshKeyStore(_tmpDir);
        await store.GenerateAsync("proj1");
        Assert.IsTrue(File.Exists(store.GetPrivateKeyPath("proj1")));
    }

    [TestMethod]
    public async Task GenerateAsync_Idempotent_OverwritesExistingKey()
    {
        var store = new SshKeyStore(_tmpDir);
        await store.GenerateAsync("proj1");
        var pub1 = await store.GetPublicKeyAsync("proj1");
        await store.GenerateAsync("proj1");
        var pub2 = await store.GetPublicKeyAsync("proj1");
        // New key is different from old one (regenerated)
        Assert.AreNotEqual(pub1, pub2);
    }
}
