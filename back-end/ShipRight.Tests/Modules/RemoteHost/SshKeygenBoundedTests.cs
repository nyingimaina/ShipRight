using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.RemoteHost;

namespace ShipRight.Tests.Modules.RemoteHost;

[TestClass]
public class SshKeygenBoundedTests
{
    private string _tmpDir = null!;

    [TestInitialize]
    public void Setup() => _tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

    [TestCleanup]
    public void Cleanup() { try { Directory.Delete(_tmpDir, true); } catch { } }

    // ── locator ───────────────────────────────────────────────────────────────

    [TestMethod]
    public void Locator_prefers_a_copy_on_PATH()
    {
        var found = SshKeygenLocator.Find(p => p == Path.Combine("C:\\bin", "ssh-keygen.exe"), ["C:\\bin"], "C:\\Windows", "C:\\Program Files", windows: true);
        Assert.AreEqual(Path.Combine("C:\\bin", "ssh-keygen.exe"), found);
    }

    [TestMethod]
    public void Locator_falls_back_to_the_windows_openssh_feature_then_git()
    {
        var win = Path.Combine("C:\\Windows", "System32", "OpenSSH", "ssh-keygen.exe");
        Assert.AreEqual(win, SshKeygenLocator.Find(p => p == win, [], "C:\\Windows", "C:\\Program Files", windows: true));

        var git = Path.Combine("C:\\Program Files", "Git", "usr", "bin", "ssh-keygen.exe");
        Assert.AreEqual(git, SshKeygenLocator.Find(p => p == git, [], "C:\\Windows", "C:\\Program Files", windows: true));
    }

    [TestMethod]
    public void Locator_returns_null_when_nothing_is_installed()
    {
        Assert.IsNull(SshKeygenLocator.Find(_ => false, ["C:\\bin"], "C:\\Windows", "C:\\Program Files", windows: true));
    }

    // ── bounded generation ────────────────────────────────────────────────────

    [TestMethod]
    public async Task GenerateBounded_creates_the_same_key_files_as_GenerateAsync()
    {
        var store = new SshKeyStore(_tmpDir);
        await store.GenerateBoundedAsync("srv1", TimeSpan.FromSeconds(30));
        Assert.IsTrue(File.Exists(Path.Combine(_tmpDir, "ssh", "srv1.pem")));
        StringAssert.StartsWith(await store.GetPublicKeyAsync("srv1"), "ssh-ed25519");
    }

    [TestMethod]
    public async Task GenerateBounded_gives_up_with_a_clear_message_instead_of_hanging()
    {
        var store = new SshKeyStore(_tmpDir);
        var ex = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => store.GenerateBoundedAsync("srv1", TimeSpan.Zero));
        StringAssert.Contains(ex.Message, "did not finish");
        Assert.IsFalse(File.Exists(Path.Combine(_tmpDir, "ssh", "srv1.pem")), "a half-made key must not be left behind");
    }

    [TestMethod]
    public async Task GenerateBounded_explains_when_ssh_keygen_is_missing()
    {
        var store = new SshKeyStore(_tmpDir);
        var ex = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => store.GenerateBoundedAsync("srv1", TimeSpan.FromSeconds(5), keygenPathOverride: Path.Combine(_tmpDir, "nope", "ssh-keygen.exe")));
        StringAssert.Contains(ex.Message, "ssh-keygen");
        StringAssert.Contains(ex.Message, "not");
    }
}
