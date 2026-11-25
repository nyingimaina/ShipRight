using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Database;

namespace ShipRight.Tests.Modules.Database;

[TestClass]
public class BackupArchiveTests
{
    private string _dir = string.Empty;
    private string _dataDir = string.Empty;
    private BackupStorage _storage = null!;
    private BackupKeyring _keyring = null!;

    [TestInitialize]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sr-backup-" + Guid.NewGuid().ToString("N"));
        _dataDir = Path.Combine(_dir, "data");
        Directory.CreateDirectory(_dataDir);
        _storage = BackupStorage.At(Path.Combine(_dir, "backups"));
        _keyring = new BackupKeyring(_dataDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private static byte[] Dump(int lines = 4000)
    {
        var text = new StringBuilder();
        for (var i = 0; i < lines; i++)
            text.Append($"INSERT INTO orders VALUES ({i}, 'customer-{i % 97}', '{new string('x', 40)}');\n");
        return Encoding.UTF8.GetBytes(text.ToString());
    }

    private Task<BackupArtifactMeta> WriteAsync(byte[] bytes, int level = 12, bool encrypt = true, string source = BackupSource.Manual)
    {
        var options = new BackupWriteOptions("p1", "shop", "sql", source, level, encrypt);
        return BackupArchive.WriteAsync(_storage, _keyring, options, new MemoryStream(bytes), null, CancellationToken.None);
    }

    [TestMethod]
    public async Task RoundTrip_CompressedAndEncrypted_PreservesEveryByte()
    {
        var bytes = Dump();

        var meta = await WriteAsync(bytes);

        Assert.AreEqual(BackupCompression.Zstd, meta.Compression);
        Assert.AreEqual(BackupEncryption.AesGcm, meta.Encryption);
        Assert.IsTrue(meta.StoredBytes < meta.OriginalBytes, "a text dump should get smaller");
        Assert.IsTrue(meta.FileName.EndsWith(".sql.zst.enc"));
        Assert.AreEqual(64, meta.Sha256.Length);

        var (stream, read) = await BackupArchive.OpenReadAsync(_storage, "p1", meta.FileName, _keyring);
        await using (stream)
        {
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy);
            CollectionAssert.AreEqual(bytes, copy.ToArray());
        }

        Assert.AreEqual(meta.Sha256, read.Sha256);
        Assert.AreEqual(bytes.Length, read.OriginalBytes);
    }

    [TestMethod]
    public async Task Write_LevelZeroAndNoEncryption_StoresTheRawBytes()
    {
        var bytes = Dump(50);

        var meta = await WriteAsync(bytes, level: 0, encrypt: false);
        Assert.AreEqual(BackupCompression.None, meta.Compression);
        Assert.AreEqual(BackupEncryption.None, meta.Encryption);
        Assert.AreEqual(bytes.Length, meta.StoredBytes);
        Assert.IsTrue(meta.FileName.EndsWith("_manual.sql"));

        var stored = await File.ReadAllBytesAsync(_storage.ArtifactPath("p1", meta.FileName));
        CollectionAssert.AreEqual(bytes, stored);
    }

    [TestMethod]
    public async Task CompressedDownload_IsDecryptableZstdWithoutShipRight()
    {
        var bytes = Dump();
        var meta = await WriteAsync(bytes);

        var (stream, _) = await BackupArchive.OpenReadAsync(
            _storage, "p1", meta.FileName, _keyring, BackupReadMode.Compressed);
        await using (stream)
        await using (var zstd = new ZstdSharp.DecompressionStream(stream))
        using (var copy = new MemoryStream())
        {
            await zstd.CopyToAsync(copy);
            CollectionAssert.AreEqual(bytes, copy.ToArray());
        }
    }

    [TestMethod]
    public async Task MissingKey_FailsWithAUsefulMessage()
    {
        var meta = await WriteAsync(Dump(40));
        var otherDataDir = Path.Combine(_dir, "other-data");
        Directory.CreateDirectory(otherDataDir);
        var otherKeyring = new BackupKeyring(otherDataDir);

        var error = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            BackupArchive.OpenReadAsync(_storage, "p1", meta.FileName, otherKeyring));

        StringAssert.Contains(error.Message, "key");
        StringAssert.Contains(error.Message, "recovery");
    }

    [TestMethod]
    public async Task TamperedFile_IsRejectedByAuthentication()
    {
        var meta = await WriteAsync(Dump(40));
        var path = _storage.ArtifactPath("p1", meta.FileName);

        var bytes = await File.ReadAllBytesAsync(path);
        bytes[^1] ^= 0xFF;
        await File.WriteAllBytesAsync(path, bytes);

        var (stream, _) = await BackupArchive.OpenReadAsync(_storage, "p1", meta.FileName, _keyring);
        await using (stream)
        {
            Exception? failure = null;
            try
            {
                using var copy = new MemoryStream();
                await stream.CopyToAsync(copy);
            }
            catch (Exception ex) { failure = ex; }

            Assert.IsNotNull(failure, "a tampered file must not decrypt as if it were intact");
        }
    }

    [TestMethod]
    public async Task List_UsesMetadata_AndRecoversLegacyFilesWithoutIt()
    {
        var bytes = Dump(20);
        var meta = await WriteAsync(bytes);

        var legacy = _storage.ArtifactPath("p1", "shop_2025-01-02_03-04-05_manual.sql");
        await File.WriteAllTextAsync(legacy, "-- legacy dump");

        var list = BackupArchive.List(_storage, "p1");

        Assert.AreEqual(2, list.Count);
        var first = list.Single(a => a.FileName == meta.FileName);
        Assert.AreEqual(meta.Sha256, first.Sha256);

        var old = list.Single(a => a.FileName == "shop_2025-01-02_03-04-05_manual.sql");
        Assert.AreEqual("shop", old.DatabaseName);
        Assert.AreEqual(BackupCompression.None, old.Compression);
        Assert.AreEqual(new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero), old.CreatedAtUtc);
    }

    [TestMethod]
    public async Task ResolveArtifact_RefusesTraversalAndOtherProjects()
    {
        await WriteAsync(Dump(10));
        Directory.CreateDirectory(_storage.ProjectDirectory("p2"));

        Assert.IsNull(_storage.ResolveArtifact("p1", "../p2/anything.sql"));
        Assert.IsNull(_storage.ResolveArtifact("p1", "..\\secret.sql"));
        Assert.IsNull(_storage.ResolveArtifact("p1", "/etc/passwd"));
        Assert.IsNull(_storage.ResolveArtifact("p1", "nope.sql"));
    }

    [TestMethod]
    public async Task Delete_RemovesTheFileAndItsMetadata()
    {
        var meta = await WriteAsync(Dump(10));

        Assert.IsTrue(BackupArchive.Delete(_storage, "p1", meta.FileName));

        Assert.IsFalse(File.Exists(_storage.ArtifactPath("p1", meta.FileName)));
        Assert.IsFalse(File.Exists(_storage.MetadataPath("p1", meta.FileName)));
        Assert.AreEqual(0, BackupArchive.List(_storage, "p1").Count);
    }

    [TestMethod]
    public async Task RotatingTheKey_KeepsOldArtifactsReadable()
    {
        var before = await WriteAsync(Dump(30));
        var newKey = await _keyring.RotateAsync();
        var after = await WriteAsync(Dump(30));

        Assert.AreNotEqual(before.KeyId, after.KeyId);
        Assert.AreEqual(newKey.Id, after.KeyId);

        foreach (var meta in new[] { before, after })
        {
            var (stream, _) = await BackupArchive.OpenReadAsync(_storage, "p1", meta.FileName, _keyring);
            await using (stream)
            {
                using var copy = new MemoryStream();
                await stream.CopyToAsync(copy);
                Assert.IsTrue(copy.Length > 0);
            }
        }
    }

    [TestMethod]
    public async Task Sha256_MatchesTheOriginalBytes()
    {
        var bytes = Dump(100);
        var meta = await WriteAsync(bytes);

        var expected = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        Assert.AreEqual(expected, meta.Sha256);
    }
}
