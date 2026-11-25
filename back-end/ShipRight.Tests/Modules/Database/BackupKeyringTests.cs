using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Database;

namespace ShipRight.Tests.Modules.Database;

[TestClass]
public class BackupKeyringTests
{
    private string _dir = string.Empty;

    [TestInitialize]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sr-keyring-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    [TestMethod]
    public async Task FirstUse_CreatesAnActiveKey()
    {
        var keyring = new BackupKeyring(_dir);

        var key = await keyring.GetActiveAsync();
        var keys = await keyring.ListAsync();

        Assert.AreEqual(32, key.Material.Length);
        Assert.AreEqual(1, keys.Count);
        Assert.IsTrue(keys[0].Active);
        Assert.AreEqual(key.Id, keys[0].Id);
    }

    [TestMethod]
    public async Task KeysAreEncryptedOnDisk()
    {
        var keyring = new BackupKeyring(_dir);
        var key = await keyring.GetActiveAsync();

        var raw = await File.ReadAllTextAsync(Path.Combine(_dir, "backup-keyring.json"));
        Assert.IsFalse(raw.Contains(Convert.ToBase64String(key.Material)), "key material must not be stored in plaintext");
        Assert.IsFalse(raw.Contains(key.Id), "even the key id is wrapped with the app's secure store");
    }

    [TestMethod]
    public async Task Restart_ReadsTheSameKey()
    {
        var first = await new BackupKeyring(_dir).GetActiveAsync();
        var second = await new BackupKeyring(_dir).GetActiveAsync();

        Assert.AreEqual(first.Id, second.Id);
        CollectionAssert.AreEqual(first.Material, second.Material);
    }

    [TestMethod]
    public async Task Rotate_AddsAKeyAndMakesItActive_WithoutLosingTheOldOne()
    {
        var keyring = new BackupKeyring(_dir);
        var original = await keyring.GetActiveAsync();

        var rotated = await keyring.RotateAsync();

        Assert.AreNotEqual(original.Id, rotated.Id);
        var keys = await keyring.ListAsync();
        Assert.AreEqual(2, keys.Count);
        Assert.IsTrue(keys.Single(k => k.Id == rotated.Id).Active);
        Assert.IsFalse(keys.Single(k => k.Id == original.Id).Active);
        CollectionAssert.AreEqual(original.Material, await keyring.FindKeyAsync(original.Id));
    }

    [TestMethod]
    public async Task Export_ContainsEveryKeySoOldBackupsStayRecoverable()
    {
        var keyring = new BackupKeyring(_dir);
        var original = await keyring.GetActiveAsync();
        var rotated = await keyring.RotateAsync();

        var file = await keyring.ExportAsync();

        Assert.AreEqual(rotated.Id, file.ActiveKeyId);
        Assert.AreEqual(2, file.Keys.Count);
        CollectionAssert.AreEquivalent(
            new[] { original.Id, rotated.Id },
            file.Keys.Select(k => k.Id).ToArray());
        Assert.IsTrue(file.Keys.All(k => Convert.FromBase64String(k.Key).Length == 32));
        Assert.AreEqual("aes256gcm", file.Encryption);
        Assert.AreEqual("SRBK1", file.Container);
    }

    [TestMethod]
    public async Task Retire_RefusesTheActiveKey_AndRemovesAnOldOne()
    {
        var keyring = new BackupKeyring(_dir);
        var original = await keyring.GetActiveAsync();

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => keyring.RetireAsync(original.Id));

        var rotated = await keyring.RotateAsync();
        await keyring.RetireAsync(original.Id);

        var keys = await keyring.ListAsync();
        Assert.AreEqual(1, keys.Count);
        Assert.AreEqual(rotated.Id, keys[0].Id);
    }

    [TestMethod]
    public async Task RecoveryFile_JSONIsStableForExternalTooling()
    {
        var keyring = new BackupKeyring(_dir);
        await keyring.GetActiveAsync();

        var json = await keyring.ExportJsonAsync();

        StringAssert.Contains(json, "\"container\": \"SRBK1\"");
        StringAssert.Contains(json, "\"encryption\": \"aes256gcm\"");
        StringAssert.Contains(json, "\"activeKeyId\"");
        StringAssert.Contains(json, "\"keys\"");
    }
}
