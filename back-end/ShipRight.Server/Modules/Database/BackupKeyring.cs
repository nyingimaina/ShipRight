using System.Security.Cryptography;
using System.Text.Json;
using Serilog;
using ShipRight.Shared.Store;

namespace ShipRight.Modules.Database;

public sealed record BackupKey(string Id, byte[] Material, DateTimeOffset CreatedAt);

/// <summary>
/// The keys that encrypt backup artifacts. One key is active for new files; older keys stay readable so a
/// rotation never makes yesterday's backup unreadable. The keyring itself is stored with the app's secure
/// store, and can be exported as a recovery file for use outside ShipRight.
/// </summary>
public sealed class BackupKeyring
{
    private const string FileName = "backup-keyring.json";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _dataDir;
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Ring? _ring;

    public BackupKeyring(string? dataDir = null)
    {
        _dataDir = dataDir ?? DataDirectory.Resolve();
        Directory.CreateDirectory(_dataDir);
        _path = Path.Combine(_dataDir, FileName);
    }

    private sealed record Ring(string ActiveKeyId, List<BackupKey> Keys);

    public async Task<BackupKey> GetActiveAsync(CancellationToken ct = default)
    {
        var ring = await LoadAsync(ct);
        return ring.Keys.First(k => k.Id == ring.ActiveKeyId);
    }

    public async Task<byte[]?> FindKeyAsync(string keyId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(keyId)) return null;
        var ring = await LoadAsync(ct);
        return ring.Keys.FirstOrDefault(k => k.Id == keyId)?.Material;
    }

    public async Task<IReadOnlyList<BackupKeyInfo>> ListAsync(CancellationToken ct = default)
    {
        var ring = await LoadAsync(ct);
        return ring.Keys.Select(k => new BackupKeyInfo(k.Id, k.CreatedAt, k.Id == ring.ActiveKeyId)).ToList();
    }

    /// <summary>Makes a fresh key active. Existing artifacts keep their old key id and stay readable.</summary>
    public async Task<BackupKey> RotateAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var ring = await LoadUnlockedAsync(ct);
            var key = NewKey();
            ring.Keys.Add(key);
            ring = ring with { ActiveKeyId = key.Id };
            await SaveAsync(ring, ct);
            Log.Information("Backup key rotated: new active key {KeyId}", key.Id);
            return key;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RetireAsync(string keyId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var ring = await LoadUnlockedAsync(ct);
            if (string.Equals(ring.ActiveKeyId, keyId, StringComparison.Ordinal))
                throw new InvalidOperationException("The active key cannot be retired. Rotate first, then retire the old key.");
            if (ring.Keys.RemoveAll(k => k.Id == keyId) == 0)
                throw new InvalidOperationException("That backup key does not exist.");
            await SaveAsync(ring, ct);
            Log.Information("Backup key {KeyId} retired", keyId);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The recovery file: every key, so artifacts written before a rotation still restore.</summary>
    public async Task<RecoveryFile> ExportAsync(CancellationToken ct = default)
    {
        var ring = await LoadAsync(ct);
        return new RecoveryFile(
            1,
            BackupEncryption.AesGcm,
            "SRBK1",
            ring.ActiveKeyId,
            DateTimeOffset.UtcNow,
            ring.Keys.Select(k => new RecoveryKey(k.Id, Convert.ToBase64String(k.Material), k.CreatedAt)).ToList());
    }

    public async Task<string> ExportJsonAsync(CancellationToken ct = default) =>
        JsonSerializer.Serialize(await ExportAsync(ct), Json);

    private async Task<Ring> LoadAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return await LoadUnlockedAsync(ct); }
        finally { _gate.Release(); }
    }

    private async Task<Ring> LoadUnlockedAsync(CancellationToken ct)
    {
        if (_ring is not null) return _ring;

        if (!File.Exists(_path))
        {
            var created = NewRing();
            await SaveAsync(created, ct);
            _ring = created;
            return created;
        }

        try
        {
            var wrapper = JsonSerializer.Deserialize<StoredRing>(await File.ReadAllTextAsync(_path, ct), Json);
            var plain = wrapper is null ? null : SecureStore.Decrypt(wrapper.Data, _dataDir);
            var ring = string.IsNullOrWhiteSpace(plain)
                ? null
                : JsonSerializer.Deserialize<Ring>(plain, Json);
            if (ring is null || ring.Keys.Count == 0 || ring.Keys.Any(k => k.Material.Length != 32))
                throw new InvalidDataException("The backup keyring is empty or damaged.");
            _ring = ring;
            return ring;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException)
        {
            throw new InvalidOperationException(
                "The backup keyring could not be read. Restore it from a recovery file before making new backups.", ex);
        }
    }

    private async Task SaveAsync(Ring ring, CancellationToken ct)
    {
        var plain = JsonSerializer.Serialize(ring, Json);
        var wrapper = new StoredRing(1, SecureStore.Encrypt(plain, _dataDir));
        var temp = _path + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(wrapper, Json), ct);
        File.Move(temp, _path, overwrite: true);
        _ring = ring;
    }

    private static Ring NewRing()
    {
        var key = NewKey();
        return new Ring(key.Id, [key]);
    }

    private static BackupKey NewKey() =>
        new(Guid.NewGuid().ToString("N"), RandomNumberGenerator.GetBytes(32), DateTimeOffset.UtcNow);

    private sealed record StoredRing(int Version, string Data);

    public sealed record BackupKeyInfo(string Id, DateTimeOffset CreatedAt, bool Active);

    public sealed record RecoveryKey(string Id, string Key, DateTimeOffset CreatedAt);

    public sealed record RecoveryFile(
        int Version,
        string Encryption,
        string Container,
        string ActiveKeyId,
        DateTimeOffset ExportedAt,
        IReadOnlyList<RecoveryKey> Keys);
}
