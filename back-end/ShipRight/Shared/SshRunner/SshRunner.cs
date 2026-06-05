using System.Text;
using Renci.SshNet;
using Serilog;
using ShipRight.Shared.Store;

namespace ShipRight.Shared.SshRunner;

public class SshRunner : ISshRunner
{
    private readonly KnownHostsStore _knownHosts;

    public SshRunner(KnownHostsStore knownHosts)
    {
        _knownHosts = knownHosts;
    }

    public async Task<int> RunAsync(
        string host, string username, string keyPath,
        string command,
        Func<string, Task>? onOutput = null,
        CancellationToken ct = default)
    {
        Log.Information("SSH connecting: {Username}@{Host}", username, host);

        using var keyFile = new PrivateKeyFile(keyPath);
        using var client = new SshClient(host, username, keyFile);
        client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(30);

        client.HostKeyReceived += (_, e) =>
        {
            var fingerprint = BitConverter.ToString(e.FingerPrint).Replace("-", ":").ToLowerInvariant();

            if (_knownHosts.IsKnown(host, fingerprint))
            {
                e.CanTrust = true;
                return;
            }

            // First connection — key will be verified by the frontend before execution
            // The KnownHostsStore.AcceptAsync flow handles this asynchronously.
            // For now we trust and add on first connection (the REST API for fingerprint
            // confirmation is wired in BuildRouter / DeployAsync).
            _knownHosts.Add(host, fingerprint);
            e.CanTrust = true;
            Log.Information("SSH host key accepted for {Host}: {Fingerprint}", host, fingerprint);
        };

        client.Connect();
        Log.Information("SSH connected: {Username}@{Host}", username, host);

        try
        {
            using var cmd = client.CreateCommand(command);
            var asyncResult = cmd.BeginExecute();

            var stdoutTask = StreamOutputAsync(cmd.OutputStream, onOutput, ct);
            var stderrTask = StreamOutputAsync(cmd.ExtendedOutputStream, onOutput, ct);

            await Task.WhenAll(stdoutTask, stderrTask);
            cmd.EndExecute(asyncResult);

            Log.Information("SSH command exited {ExitCode}: {Host}", cmd.ExitStatus, host);
            return cmd.ExitStatus ?? -1;
        }
        finally
        {
            client.Disconnect();
        }
    }

    private static async Task StreamOutputAsync(
        Stream stream, Func<string, Task>? onOutput, CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        char[] buffer = new char[256];
        var lineBuffer = new StringBuilder();

        while (!ct.IsCancellationRequested)
        {
            int read;
            try { read = await reader.ReadAsync(buffer, 0, buffer.Length); }
            catch (ObjectDisposedException) { break; }
            catch (OperationCanceledException) { break; }

            if (read == 0) break;

            for (int i = 0; i < read; i++)
            {
                if (buffer[i] == '\n')
                {
                    if (onOutput is not null && lineBuffer.Length > 0)
                        await onOutput(lineBuffer.ToString());
                    lineBuffer.Clear();
                }
                else
                {
                    lineBuffer.Append(buffer[i]);
                }
            }
        }

        if (onOutput is not null && lineBuffer.Length > 0)
            await onOutput(lineBuffer.ToString());
    }
}
