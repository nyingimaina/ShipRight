using System.Text;
using Serilog;
using ShipRight.Modules.Projects;
using ShipRight.Shared.SshRunner;

namespace ShipRight.Modules.RemoteHost;

public sealed record RemoteEntry(string Name, string Path, bool IsDirectory, long Size, DateTime? Modified);
public sealed record RemoteListing(string Path, string? Parent, IReadOnlyList<RemoteEntry> Entries);

/// <summary>A user-facing failure with the HTTP status it should map to.</summary>
public sealed class RemoteFileException(string message, int statusCode = 400) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>Low-level file transfer to a server (SFTP in production, a fake in tests).</summary>
public interface IRemoteFileClient
{
    Task<IReadOnlyList<RemoteEntry>> ListAsync(ServerConfig server, string path, CancellationToken ct);
    Task<RemoteEntry?> StatAsync(ServerConfig server, string path, CancellationToken ct);
    Task ReadAsync(ServerConfig server, string path, Stream destination, CancellationToken ct);
    Task WriteAsync(ServerConfig server, string path, Stream source, CancellationToken ct);
}

/// <summary>
/// Files tab operations on a saved server: list, open (text), download (folders are tar.gz'd on the
/// server first), upload and delete. All policy lives here — path normalisation, size caps and
/// protection of the home / working directories — so it is unit-testable without SSH.
/// </summary>
public sealed class RemoteFileService
{
    public const long MaxReadBytes = 512 * 1024;
    public const long MaxUploadBytes = 200L * 1024 * 1024;

    private readonly IRemoteFileClient _client;
    private readonly ISshRunner _ssh;

    public RemoteFileService(IRemoteFileClient client, ISshRunner ssh)
    {
        _client = client;
        _ssh = ssh;
    }

    public static string ShellQuote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    public static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0'))
            throw new RemoteFileException("Invalid path.");
        if (!path.StartsWith('/'))
            throw new RemoteFileException("Path must be absolute (start with /).");

        var stack = new List<string>();
        foreach (var seg in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (seg == ".") continue;
            if (seg == "..") { if (stack.Count > 0) stack.RemoveAt(stack.Count - 1); continue; }
            stack.Add(seg);
        }
        return "/" + string.Join('/', stack);
    }

    private static string? ParentOf(string path)
    {
        if (path == "/") return null;
        var i = path.LastIndexOf('/');
        return i <= 0 ? "/" : path[..i];
    }

    private static string DefaultDir(ServerConfig s) =>
        string.IsNullOrWhiteSpace(s.RemoteWorkingDir) ? $"/home/{s.Username}" : NormalizePath(s.RemoteWorkingDir);

    private async Task<RemoteEntry> RequireAsync(ServerConfig s, string path, CancellationToken ct) =>
        await _client.StatAsync(s, path, ct) ?? throw new RemoteFileException("Not found on the server.", 404);

    public async Task<RemoteListing> ListAsync(ServerConfig s, string? path, bool showHidden, CancellationToken ct = default)
    {
        var dir = string.IsNullOrWhiteSpace(path) ? DefaultDir(s) : NormalizePath(path);
        var all = await _client.ListAsync(s, dir, ct);
        var entries = all
            .Where(e => showHidden || !e.Name.StartsWith('.'))
            .OrderByDescending(e => e.IsDirectory)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new(dir, ParentOf(dir), entries);
    }

    public async Task<string> ReadTextAsync(ServerConfig s, string path, CancellationToken ct = default)
    {
        var p = NormalizePath(path);
        var entry = await RequireAsync(s, p, ct);
        if (entry.IsDirectory) throw new RemoteFileException("That is a folder, not a file.");
        if (entry.Size > MaxReadBytes)
            throw new RemoteFileException($"File is too large to open here (over {MaxReadBytes / 1024} KB). Download it instead.");

        using var ms = new MemoryStream();
        await _client.ReadAsync(s, p, ms, ct);
        var bytes = ms.ToArray();
        if (Array.IndexOf(bytes, (byte)0) >= 0)
            throw new RemoteFileException("This looks like a binary file. Download it instead.");
        return Encoding.UTF8.GetString(bytes);
    }

    public async Task<string> DownloadNameAsync(ServerConfig s, string path, CancellationToken ct = default)
    {
        var entry = await RequireAsync(s, NormalizePath(path), ct);
        return entry.IsDirectory ? entry.Name + ".tar.gz" : entry.Name;
    }

    public async Task DownloadAsync(ServerConfig s, string path, Stream destination, CancellationToken ct = default)
    {
        var p = NormalizePath(path);
        var entry = await RequireAsync(s, p, ct);
        if (!entry.IsDirectory)
        {
            await _client.ReadAsync(s, p, destination, ct);
            return;
        }

        var tmp = $"/tmp/shipright-{Guid.NewGuid():N}.tar.gz";
        var parent = ParentOf(p) ?? "/";
        var tar = $"tar -czf {ShellQuote(tmp)} -C {ShellQuote(parent)} -- {ShellQuote(entry.Name)}";
        try
        {
            var exit = await _ssh.RunAsync(s.Host, s.Username, s.SshKeyPath, tar, ct: ct);
            if (exit != 0) throw new RemoteFileException($"Could not compress the folder on the server (exit {exit}).", 502);
            await _client.ReadAsync(s, tmp, destination, ct);
        }
        finally
        {
            try { await _ssh.RunAsync(s.Host, s.Username, s.SshKeyPath, $"rm -f {ShellQuote(tmp)}", ct: CancellationToken.None); }
            catch (Exception ex) { Log.Warning(ex, "Could not remove temp archive {Tmp} on {Host}", tmp, s.Host); }
        }
    }

    public async Task UploadAsync(ServerConfig s, string directory, string fileName, Stream content,
        long maxBytes = MaxUploadBytes, CancellationToken ct = default)
    {
        var dir = NormalizePath(directory);
        if (string.IsNullOrWhiteSpace(fileName) || fileName is "." or ".." || fileName.Contains('/') || fileName.Contains('\\') || fileName.Contains('\0'))
            throw new RemoteFileException("Invalid file name.");
        var target = await RequireAsync(s, dir, ct);
        if (!target.IsDirectory) throw new RemoteFileException("The upload target is not a folder.");
        if (content.CanSeek && content.Length > maxBytes)
            throw new RemoteFileException($"File is too large to upload (limit {maxBytes / (1024 * 1024)} MB).", 413);

        await _client.WriteAsync(s, (dir == "/" ? "" : dir) + "/" + fileName, new LimitedStream(content, maxBytes), ct);
    }

    public async Task DeleteAsync(ServerConfig s, string path, CancellationToken ct = default)
    {
        var p = NormalizePath(path);
        var depth = p.Split('/', StringSplitOptions.RemoveEmptyEntries).Length;
        var protectedPaths = new[] { DefaultDir(s), $"/home/{s.Username}", "/root" };
        if (depth < 3 || protectedPaths.Contains(p))
            throw new RemoteFileException("That location is protected and cannot be deleted from here.", 403);

        await RequireAsync(s, p, ct);
        var exit = await _ssh.RunAsync(s.Host, s.Username, s.SshKeyPath, $"rm -rf -- {ShellQuote(p)}", ct: ct);
        if (exit != 0) throw new RemoteFileException($"Delete failed on the server (exit {exit}).", 502);
        Log.Information("Deleted {Path} on server {ServerId}", p, s.Id);
    }

    /// <summary>Throws once more than the allowed bytes have been read (guards non-seekable uploads).</summary>
    private sealed class LimitedStream(Stream inner, long max) : Stream
    {
        private long _read;
        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = inner.Read(buffer, offset, count);
            Count(n);
            return n;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var n = await inner.ReadAsync(buffer, ct);
            Count(n);
            return n;
        }
        private void Count(int n)
        {
            _read += n;
            if (_read > max) throw new RemoteFileException($"File is too large to upload (limit {max / (1024 * 1024)} MB).", 413);
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
