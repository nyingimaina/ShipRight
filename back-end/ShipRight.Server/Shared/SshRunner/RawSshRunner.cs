using System.Text;
using Renci.SshNet;
using Serilog;
using ShipRight.Shared.Store;

namespace ShipRight.Shared.SshRunner;

/// <summary>Direct-route raw byte SSH runner. Mirrors <see cref="SshRunner"/> but never decodes stdout.</summary>
public sealed class RawSshRunner(KnownHostsStore knownHosts) : IRawSshRunner
{
    public async Task<int> RunAsync(
        string host, string username, string keyPath, string command, Stream stdout,
        Func<string, Task>? onStderr = null, CancellationToken ct = default)
    {
        using var keyFile = await SshKeyLoader.LoadAsync(keyPath);
        using var client = new SshClient(host, username, keyFile);
        client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(30);
        client.HostKeyReceived += (_, e) =>
        {
            var fingerprint = BitConverter.ToString(e.FingerPrint).Replace("-", ":").ToLowerInvariant();
            if (!knownHosts.IsKnown(host, fingerprint)) knownHosts.Add(host, fingerprint);
            e.CanTrust = true;
        };

        client.Connect();
        try
        {
            using var cmd = client.CreateCommand(command);
            var asyncResult = cmd.BeginExecute();
            var output = cmd.OutputStream.CopyToAsync(stdout, 64 * 1024, ct);
            var error = StreamStderrAsync(cmd.ExtendedOutputStream, onStderr, ct);
            await Task.WhenAll(output, error);
            cmd.EndExecute(asyncResult);
            return cmd.ExitStatus ?? -1;
        }
        finally
        {
            client.Disconnect();
        }
    }

    internal static async Task StreamStderrAsync(Stream stream, Func<string, Task>? onLine, CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        while (!ct.IsCancellationRequested)
        {
            string? line;
            try { line = await reader.ReadLineAsync(ct); }
            catch (ObjectDisposedException) { break; }
            catch (OperationCanceledException) { break; }
            if (line is null) break;
            if (onLine is not null) await onLine(line);
        }
    }
}
