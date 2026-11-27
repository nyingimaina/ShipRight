using System.Text.Json;
using ZstdSharp;

namespace ShipRight.Modules.Database;

/// <summary>
/// Opens a backup artifact the user downloaded or copied, using a recovery file for the key. Handles the
/// encrypted container (<c>SRBK1</c>), plain files, and zstd-compressed files without encryption.
/// </summary>
public static class BackupDecryptor
{
    private static readonly byte[] Magic = "SRBK1"u8.ToArray();
    private static readonly byte[] ZstdMagic = [0x28, 0xB5, 0x2F, 0xFD];

    public static async Task<(Stream Stream, string FileName)> OpenAsync(
        string artifactPath, string? recoveryJson, CancellationToken ct = default, string? originalName = null)
    {
        var recovery = string.IsNullOrWhiteSpace(recoveryJson) ? null : ParseRecovery(recoveryJson);
        var file = File.Open(artifactPath, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
        });

        try
        {
            var header = new byte[Magic.Length];
            var read = await file.ReadAsync(header, ct);
            file.Position = 0;

            Stream plain = file;
            if (read == Magic.Length && header.SequenceEqual(Magic))
            {
                if (recovery is null)
                    throw new InvalidOperationException(
                        "This backup is encrypted. Choose the JSON recovery file that was exported from ShipRight.");
                plain = new BackupDecryptStream(file, id =>
                    recovery.TryGetValue(id, out var key) ? key : null);
            }

            // The decrypted bytes may still be zstd. Peek four bytes and put them back for the decompressor.
            var peek = new byte[ZstdMagic.Length];
            var peeked = await plain.ReadAsync(peek, ct);
            plain = new PrefixedStream(peek.AsSpan(0, peeked).ToArray(), plain);

            if (peeked == ZstdMagic.Length && peek.AsSpan().SequenceEqual(ZstdMagic))
                plain = new DecompressionStream(plain, leaveOpen: false);

            var chain = ReferenceEquals(plain, file) ? new StreamChain(file) : new StreamChain(file, plain);
            return (chain, DecryptedName(originalName ?? Path.GetFileName(artifactPath)));
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    public static string DecryptedName(string artifactName)
    {
        var name = artifactName;
        if (name.EndsWith(".enc", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        if (name.EndsWith(".zst", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return name;
    }

    /// <summary>Validates the exported recovery file and returns key id → key material.</summary>
    public static Dictionary<string, byte[]> ParseRecovery(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("That file is not a ShipRight recovery file (it is not JSON).");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !TryGet(root, "keys", out var keys) || keys.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException(
                    "That JSON file has no 'keys' list, so it is not a ShipRight recovery file.");

            var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var entry in keys.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object ||
                    !TryGet(entry, "id", out var idNode) ||
                    !TryGet(entry, "key", out var keyNode))
                    throw new InvalidOperationException("The recovery file has a key entry without an id or key.");

                var id = idNode.GetString();
                var base64 = keyNode.GetString();
                byte[] material;
                try
                {
                    material = Convert.FromBase64String(base64 ?? string.Empty);
                }
                catch (FormatException)
                {
                    throw new InvalidOperationException($"The recovery key '{id}' is not valid base64.");
                }

                if (string.IsNullOrWhiteSpace(id) || material.Length != 32)
                    throw new InvalidOperationException($"The recovery key '{id}' is not a 256-bit key.");

                result[id] = material;
            }

            if (result.Count == 0)
                throw new InvalidOperationException("The recovery file contains no keys.");

            return result;
        }
    }

    private static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}

/// <summary>Replays a few already-read bytes before the rest of a stream (used to sniff zstd without losing data).</summary>
public sealed class PrefixedStream(byte[] prefix, Stream inner) : Stream
{
    private readonly byte[] _prefix = prefix;
    private int _offset;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_offset < _prefix.Length)
        {
            var take = Math.Min(buffer.Length, _prefix.Length - _offset);
            _prefix.AsSpan(_offset, take).CopyTo(buffer);
            _offset += take;
            return take;
        }

        return inner.Read(buffer);
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_offset < _prefix.Length)
        {
            var take = Math.Min(buffer.Length, _prefix.Length - _offset);
            _prefix.AsSpan(_offset, take).CopyTo(buffer.Span);
            _offset += take;
            return ValueTask.FromResult(take);
        }

        return inner.ReadAsync(buffer, ct);
    }

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
}
