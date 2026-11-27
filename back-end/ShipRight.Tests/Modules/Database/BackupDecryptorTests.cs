using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Database;

namespace ShipRight.Tests.Modules.Database;

[TestClass]
public class BackupDecryptorTests
{
    private string _dir = string.Empty;
    private BackupStorage _storage = null!;
    private BackupKeyring _keyring = null!;

    [TestInitialize]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sr-decrypt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_dir, "data"));
        _storage = BackupStorage.At(Path.Combine(_dir, "backups"));
        _keyring = new BackupKeyring(Path.Combine(_dir, "data"));
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private static byte[] Dump()
    {
        var text = new StringBuilder();
        for (var i = 0; i < 2000; i++)
            text.Append($"INSERT INTO orders VALUES ({i}, 'customer-{i % 50}', '{new string('y', 60)}');\n");
        return Encoding.UTF8.GetBytes(text.ToString());
    }

    private async Task<BackupArtifactMeta> WriteAsync(byte[] bytes, int level, bool encrypt)
    {
        var options = new BackupWriteOptions("p1", "shop", "sql", BackupSource.Manual, level, encrypt);
        return await BackupArchive.WriteAsync(_storage, _keyring, options, new MemoryStream(bytes), null,
            CancellationToken.None);
    }

    private async Task<byte[]> ReadAllAsync(string path, string? recovery)
    {
        var (stream, _) = await BackupDecryptor.OpenAsync(path, recovery);
        await using (stream)
        {
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy);
            return copy.ToArray();
        }
    }

    [TestMethod]
    public async Task EncryptedAndCompressed_RecoversTheOriginalBytes()
    {
        var bytes = Dump();
        var meta = await WriteAsync(bytes, 12, true);
        var recovery = await _keyring.ExportJsonAsync();

        var restored = await ReadAllAsync(_storage.ArtifactPath("p1", meta.FileName), recovery);

        CollectionAssert.AreEqual(bytes, restored);
        Assert.AreEqual("shop_2026-01-01_00-00-00_manual.sql", BackupDecryptor.DecryptedName("shop_2026-01-01_00-00-00_manual.sql.zst.enc"));
    }

    [TestMethod]
    public async Task WrongRecoveryFile_FailsWithAUsefulMessage()
    {
        var meta = await WriteAsync(Dump(), 12, true);
        var otherDir = Path.Combine(_dir, "other");
        Directory.CreateDirectory(otherDir);
        var otherRecovery = await new BackupKeyring(otherDir).ExportJsonAsync();

        var error = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            BackupDecryptor.OpenAsync(_storage.ArtifactPath("p1", meta.FileName), otherRecovery));

        StringAssert.Contains(error.Message, "key");
    }

    [TestMethod]
    public async Task EncryptedWithoutRecoveryFile_ExplainsWhatIsMissing()
    {
        var meta = await WriteAsync(Dump(), 12, true);

        var error = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            BackupDecryptor.OpenAsync(_storage.ArtifactPath("p1", meta.FileName), null));

        StringAssert.Contains(error.Message, "recovery");
    }

    [TestMethod]
    public async Task ZstdOnlyFile_IsDecompressedWithoutAKey()
    {
        var bytes = Dump();
        var meta = await WriteAsync(bytes, 12, false);

        var restored = await ReadAllAsync(_storage.ArtifactPath("p1", meta.FileName), null);

        CollectionAssert.AreEqual(bytes, restored);
    }

    [TestMethod]
    public async Task OpenAsync_KeepsTheOriginalUploadedNameForTheDownload()
    {
        var meta = await WriteAsync(Dump(), 12, false);

        var (stream, name) = await BackupDecryptor.OpenAsync(
            _storage.ArtifactPath("p1", meta.FileName), null, default, "dailies_2026-11-27_02-00-04_schedule.sql.zst");
        await stream.DisposeAsync();

        Assert.AreEqual("dailies_2026-11-27_02-00-04_schedule.sql", name);
    }

    [TestMethod]
    public async Task PlainFile_PassesThroughUnchanged()
    {
        var path = Path.Combine(_dir, "plain.sql");
        var bytes = Encoding.UTF8.GetBytes("-- plain dump\nCREATE TABLE t (id int);\n");
        await File.WriteAllBytesAsync(path, bytes);

        var restored = await ReadAllAsync(path, null);

        CollectionAssert.AreEqual(bytes, restored);
    }

    [TestMethod]
    public void ParseRecovery_RejectsFilesThatAreNotRecoveryFiles()
    {
        Assert.ThrowsException<InvalidOperationException>(() => BackupDecryptor.ParseRecovery("not json"));
        Assert.ThrowsException<InvalidOperationException>(() => BackupDecryptor.ParseRecovery("{\"hello\":true}"));
        Assert.ThrowsException<InvalidOperationException>(() =>
            BackupDecryptor.ParseRecovery("{\"keys\":[{\"id\":\"a\",\"key\":\"not-base64\"}]}"));
    }

    [TestMethod]
    public async Task TamperedArtifact_DoesNotProducePlaintext()
    {
        var meta = await WriteAsync(Dump(), 12, true);
        var recovery = await _keyring.ExportJsonAsync();
        var path = _storage.ArtifactPath("p1", meta.FileName);

        var bytes = await File.ReadAllBytesAsync(path);
        bytes[^20] ^= 0xFF;
        await File.WriteAllBytesAsync(path, bytes);

        Exception? failure = null;
        try
        {
            var (stream, _) = await BackupDecryptor.OpenAsync(path, recovery);
            await using (stream)
            {
                using var copy = new MemoryStream();
                await stream.CopyToAsync(copy);
            }
        }
        catch (Exception ex) { failure = ex; }

        Assert.IsNotNull(failure, "a changed artifact must not decrypt into silent garbage");
    }
}
