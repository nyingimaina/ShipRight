using System.Buffers.Binary;
using System.Security.Cryptography;

namespace ShipRight.Modules.Database;

/// <summary>
/// Streaming AES-256-GCM for backup files. GCM needs one buffer per message, so the file is sealed in
/// 1 MiB chunks with a per-file random prefix and a chunk counter as the nonce; any change to the file
/// fails authentication on read instead of returning damaged data.
/// </summary>
public sealed class BackupEncryptStream : Stream
{
    private static readonly byte[] Magic = "SRBK1"u8.ToArray();
    private const int ChunkSize = 1024 * 1024;

    private readonly Stream _inner;
    private readonly AesGcm _aes;
    private readonly byte[] _noncePrefix = RandomNumberGenerator.GetBytes(4);
    private readonly byte[] _buffer = new byte[ChunkSize];
    private readonly byte[] _cipher = new byte[ChunkSize];
    private readonly byte[] _tag = new byte[16];
    private readonly byte[] _nonce = new byte[12];
    private int _buffered;
    private long _counter;
    private bool _headerWritten;
    private bool _completed;

    public BackupEncryptStream(Stream inner, byte[] key, string keyId)
    {
        if (key.Length != 32) throw new ArgumentException("The backup key must be 32 bytes.", nameof(key));
        _inner = inner;
        _aes = new AesGcm(key, 16);
        KeyId = keyId;
    }

    public string KeyId { get; }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        EnsureHeader();
        while (!buffer.IsEmpty)
        {
            var take = Math.Min(ChunkSize - _buffered, buffer.Length);
            buffer[..take].CopyTo(_buffer.AsSpan(_buffered));
            _buffered += take;
            buffer = buffer[take..];
            if (_buffered == ChunkSize) FlushChunk();
        }
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await EnsureHeaderAsync(cancellationToken);
        while (!buffer.IsEmpty)
        {
            var take = Math.Min(ChunkSize - _buffered, buffer.Length);
            buffer.Span[..take].CopyTo(_buffer.AsSpan(_buffered));
            _buffered += take;
            buffer = buffer[take..];
            if (_buffered == ChunkSize) await FlushChunkAsync(cancellationToken);
        }
    }

    private void EnsureHeader()
    {
        if (_headerWritten) return;
        Span<byte> length = stackalloc byte[1];
        length[0] = (byte)KeyId.Length;
        _inner.Write(Magic);
        _inner.WriteByte(1);
        _inner.Write(length);
        _inner.Write(global::System.Text.Encoding.ASCII.GetBytes(KeyId));
        _headerWritten = true;
    }

    private async Task EnsureHeaderAsync(CancellationToken ct)
    {
        if (_headerWritten) return;
        var header = new byte[Magic.Length + 2 + KeyId.Length];
        Magic.CopyTo(header, 0);
        header[Magic.Length] = 1;
        header[Magic.Length + 1] = (byte)KeyId.Length;
        global::System.Text.Encoding.ASCII.GetBytes(KeyId).CopyTo(header, Magic.Length + 2);
        await _inner.WriteAsync(header, ct);
        _headerWritten = true;
    }

    private void FlushChunk()
    {
        if (_buffered == 0) return;
        Seal();
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, _buffered);
        _inner.Write(length);
        _inner.Write(_nonce);
        _inner.Write(_tag);
        _inner.Write(_cipher.AsSpan(0, _buffered));
        _buffered = 0;
    }

    private async Task FlushChunkAsync(CancellationToken ct)
    {
        if (_buffered == 0) return;
        Seal();
        var header = new byte[4 + 12 + 16];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0, 4), _buffered);
        _nonce.CopyTo(header, 4);
        _tag.CopyTo(header, 16);
        await _inner.WriteAsync(header, ct);
        await _inner.WriteAsync(_cipher.AsMemory(0, _buffered), ct);
        _buffered = 0;
    }

    private void Seal()
    {
        BinaryPrimitives.WriteInt64BigEndian(_nonce.AsSpan(4), _counter++);
        _noncePrefix.CopyTo(_nonce, 0);
        _aes.Encrypt(_nonce, _buffer.AsSpan(0, _buffered), _cipher.AsSpan(0, _buffered), _tag);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try
            {
                if (!_completed)
                {
                    _completed = true;
                    EnsureHeader();
                    FlushChunk();
                    Span<byte> end = stackalloc byte[4];
                    BinaryPrimitives.WriteInt32BigEndian(end, 0);
                    _inner.Write(end);
                    _inner.Flush();
                }
            }
            finally
            {
                _aes.Dispose();
            }
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        try
        {
            if (!_completed)
            {
                _completed = true;
                await EnsureHeaderAsync(CancellationToken.None);
                await FlushChunkAsync(CancellationToken.None);
                var end = new byte[4];
                await _inner.WriteAsync(end);
                await _inner.FlushAsync();
            }
        }
        finally
        {
            _aes.Dispose();
        }

        GC.SuppressFinalize(this);
    }
}

/// <summary>Read side of <see cref="BackupEncryptStream"/>.</summary>
public sealed class BackupDecryptStream : Stream
{
    private static readonly byte[] Magic = "SRBK1"u8.ToArray();

    private readonly Stream _inner;
    private readonly AesGcm _aes;
    private readonly byte[] _buffer = new byte[1024 * 1024];
    private readonly byte[] _cipher = new byte[1024 * 1024];
    private readonly byte[] _tag = new byte[16];
    private readonly byte[] _nonce = new byte[12];
    private int _offset;
    private int _count;
    private bool _finished;

    public BackupDecryptStream(Stream inner, Func<string, byte[]?> keyFor)
    {
        _inner = inner;
        ReadHeader(keyFor);
        _aes = new AesGcm(Key, 16);
    }

    public string KeyId { get; private set; } = string.Empty;
    private byte[] Key { get; set; } = [];

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override int Read(Span<byte> buffer)
    {
        if (_offset < _count)
        {
            var take = Math.Min(buffer.Length, _count - _offset);
            _buffer.AsSpan(_offset, take).CopyTo(buffer);
            _offset += take;
            return take;
        }

        if (_finished || buffer.IsEmpty) return 0;

        var header = new byte[4];
        _inner.ReadExactly(header);
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length == 0)
        {
            _finished = true;
            return 0;
        }
        if (length < 0 || length > _cipher.Length)
            throw new InvalidDataException("The backup file is damaged (bad chunk length).");

        _inner.ReadExactly(_nonce);
        _inner.ReadExactly(_tag);
        _inner.ReadExactly(_cipher.AsSpan(0, length));
        _aes.Decrypt(_nonce, _cipher.AsSpan(0, length), _tag, _buffer.AsSpan(0, length));
        _offset = 0;
        _count = length;
        return Read(buffer);
    }

    private void ReadHeader(Func<string, byte[]?> keyFor)
    {
        var magic = new byte[Magic.Length];
        _inner.ReadExactly(magic);
        if (!magic.SequenceEqual(Magic)) throw new InvalidDataException("This is not a ShipRight backup file.");
        var version = _inner.ReadByte();
        if (version != 1) throw new InvalidDataException($"Unsupported backup file version {version}.");
        var idLength = _inner.ReadByte();
        var id = new byte[idLength];
        _inner.ReadExactly(id);
        KeyId = global::System.Text.Encoding.ASCII.GetString(id);
        Key = keyFor(KeyId) ?? throw new InvalidDataException($"The key '{KeyId}' for this backup is missing.");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _aes.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>Disposes every link of a stream chain in order.</summary>
public sealed class StreamChain(params Stream[] streams) : Stream
{
    private readonly Stream _last = streams[^1];

    public override bool CanRead => _last.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => _last.CanWrite;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => _last.Flush();
    public override int Read(byte[] buffer, int offset, int count) => _last.Read(buffer, offset, count);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => _last.ReadAsync(buffer, ct);
    public override void Write(byte[] buffer, int offset, int count) => _last.Write(buffer, offset, count);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => _last.WriteAsync(buffer, ct);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var stream in streams.Reverse())
            {
                try { stream.Dispose(); } catch { /* best-effort */ }
            }
        }
        base.Dispose(disposing);
    }
}
