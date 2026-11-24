using Renci.SshNet;
using Renci.SshNet.Common;
using ShipRight.Modules.Projects;
using ShipRight.Modules.ServerNetwork;
using ShipRight.Shared.SshRunner;

namespace ShipRight.Modules.RemoteHost;

/// <summary>
/// The Files tab's transport. Same behaviour as <see cref="SftpRemoteFileClient"/>, but the connection follows the
/// server's network route, so a server assigned to a tailnet is reached through the tailscaled SOCKS5 proxy.
/// </summary>
public sealed class RoutedSftpRemoteFileClient(IServerNetworkResolver resolver, KnownHostsStore knownHosts) : IRemoteFileClient
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    private static RemoteEntry ToEntry(Renci.SshNet.Sftp.ISftpFile f) =>
        new(f.Name, f.FullName, f.IsDirectory, f.Length, f.LastWriteTimeUtc);

    private static RemoteFileException Wrap(Exception ex) => ex switch
    {
        SftpPathNotFoundException => new("Not found on the server.", 404),
        SftpPermissionDeniedException => new("Permission denied on the server.", 403),
        _ => new($"SSH error: {ex.Message}", 502),
    };

    private async Task<T> WithClientAsync<T>(ServerConfig s, Func<SftpClient, T> work, CancellationToken ct)
    {
        using var connection = await RoutedSftp.OpenAsync(resolver, s, knownHosts, ConnectTimeout, ct);
        try { return work(connection.Client); }
        catch (RemoteFileException) { throw; }
        catch (Exception ex) when (ex is SshException or IOException) { throw Wrap(ex); }
    }

    public Task<IReadOnlyList<RemoteEntry>> ListAsync(ServerConfig s, string path, CancellationToken ct) =>
        WithClientAsync<IReadOnlyList<RemoteEntry>>(s, c => c.ListDirectory(path)
            .Where(f => f.Name is not "." and not "..").Select(ToEntry).ToList(), ct);

    public Task<RemoteEntry?> StatAsync(ServerConfig s, string path, CancellationToken ct) =>
        WithClientAsync<RemoteEntry?>(s, c => c.Exists(path) ? ToEntry(c.Get(path)) : null, ct);

    public Task ReadAsync(ServerConfig s, string path, Stream destination, CancellationToken ct) =>
        WithClientAsync(s, c => { c.DownloadFile(path, destination); return 0; }, ct);

    public Task WriteAsync(ServerConfig s, string path, Stream source, CancellationToken ct) =>
        WithClientAsync(s, c => { c.UploadFile(source, path, canOverride: true); return 0; }, ct);
}
