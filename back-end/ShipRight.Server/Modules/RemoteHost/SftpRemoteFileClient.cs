using Renci.SshNet;
using Renci.SshNet.Common;
using ShipRight.Modules.Projects;
using ShipRight.Shared.SshRunner;

namespace ShipRight.Modules.RemoteHost;

/// <summary>SFTP-backed <see cref="IRemoteFileClient"/>. One short-lived connection per call.</summary>
public sealed class SftpRemoteFileClient : IRemoteFileClient
{
    private const int ConnectTimeoutSecs = 15;
    private readonly KnownHostsStore _knownHosts;

    public SftpRemoteFileClient(KnownHostsStore knownHosts) => _knownHosts = knownHosts;

    private async Task<(SftpClient Client, IDisposable Key)> ConnectAsync(ServerConfig s)
    {
        var key = await SshKeyLoader.LoadAsync(s.SshKeyPath);
        var sftp = new SftpClient(s.Host, s.Username, key);
        sftp.ConnectionInfo.Timeout = TimeSpan.FromSeconds(ConnectTimeoutSecs);
        sftp.HostKeyReceived += (_, e) =>
        {
            var fp = BitConverter.ToString(e.FingerPrint).Replace("-", ":").ToLowerInvariant();
            if (!_knownHosts.IsKnown(s.Host, fp)) _knownHosts.Add(s.Host, fp);
            e.CanTrust = true;
        };
        sftp.Connect();
        return (sftp, key);
    }

    private static RemoteEntry ToEntry(Renci.SshNet.Sftp.ISftpFile f) =>
        new(f.Name, f.FullName, f.IsDirectory, f.Length, f.LastWriteTimeUtc);

    private static RemoteFileException Wrap(Exception ex) => ex switch
    {
        SftpPathNotFoundException => new("Not found on the server.", 404),
        SftpPermissionDeniedException => new("Permission denied on the server.", 403),
        _ => new($"SSH error: {ex.Message}", 502),
    };

    private async Task<T> WithClientAsync<T>(ServerConfig s, Func<SftpClient, T> work)
    {
        var (sftp, key) = await ConnectAsync(s);
        try { return work(sftp); }
        catch (RemoteFileException) { throw; }
        catch (Exception ex) when (ex is SshException or IOException) { throw Wrap(ex); }
        finally { if (sftp.IsConnected) sftp.Disconnect(); sftp.Dispose(); key.Dispose(); }
    }

    public Task<IReadOnlyList<RemoteEntry>> ListAsync(ServerConfig s, string path, CancellationToken ct) =>
        WithClientAsync<IReadOnlyList<RemoteEntry>>(s, c => c.ListDirectory(path)
            .Where(f => f.Name is not "." and not "..").Select(ToEntry).ToList());

    public Task<RemoteEntry?> StatAsync(ServerConfig s, string path, CancellationToken ct) =>
        WithClientAsync<RemoteEntry?>(s, c => c.Exists(path) ? ToEntry(c.Get(path)) : null);

    public Task ReadAsync(ServerConfig s, string path, Stream destination, CancellationToken ct) =>
        WithClientAsync(s, c => { c.DownloadFile(path, destination); return 0; });

    public Task WriteAsync(ServerConfig s, string path, Stream source, CancellationToken ct) =>
        WithClientAsync(s, c => { c.UploadFile(source, path, canOverride: true); return 0; });
}
