using Renci.SshNet;
using Serilog;

namespace ShipRight.Modules.RemoteHost;

public class LinuxSshProvider : IRemoteHostProvider
{
    public string ProviderType => "linux-ssh";

    public async Task AuthorizeKeyAsync(
        RemoteHostConfig config, string password, string publicKey, CancellationToken ct = default)
    {
        Log.Information("SSH authorize: {Username}@{Host}:{Port} (password auth)",
            config.Username, config.Host, config.Port);

        var connInfo = new Renci.SshNet.ConnectionInfo(
            config.Host, config.Port, config.Username,
            new PasswordAuthenticationMethod(config.Username, password));
        connInfo.Timeout = TimeSpan.FromSeconds(30);

        using var client = new SshClient(connInfo);
        await Task.Run(() => client.Connect(), ct);

        try
        {
            var cmd = BuildAuthorizeCommand(publicKey);
            using var sshCmd = client.CreateCommand(cmd);
            await Task.Run(() => sshCmd.Execute(), ct);

            if (sshCmd.ExitStatus != 0)
                throw new InvalidOperationException(
                    $"Key authorization command failed (exit {sshCmd.ExitStatus}): {sshCmd.Error?.Trim()}");

            Log.Information("SSH key authorized on {Host}", config.Host);
        }
        finally
        {
            client.Disconnect();
        }
    }

    public async Task<bool> IsKeyAuthorizedAsync(
        RemoteHostConfig config, string keyPath, CancellationToken ct = default)
    {
        try
        {
            using var keyFile = new PrivateKeyFile(keyPath);
            var connInfo = new Renci.SshNet.ConnectionInfo(
                config.Host, config.Port, config.Username,
                new PrivateKeyAuthenticationMethod(config.Username, keyFile));
            connInfo.Timeout = TimeSpan.FromSeconds(10);

            using var client = new SshClient(connInfo);
            await Task.Run(() => client.Connect(), ct);
            client.Disconnect();
            Log.Information("SSH key authorization confirmed for {Host}", config.Host);
            return true;
        }
        catch (Exception ex)
        {
            Log.Information("SSH key not authorized for {Host}: {Message}", config.Host, ex.Message);
            return false;
        }
    }

    internal static string BuildAuthorizeCommand(string publicKey)
    {
        // Single-quote the key; escape any embedded single-quotes (edge case — Ed25519 keys don't have them)
        var escaped = publicKey.Replace("'", "'\\''");
        return $"mkdir -p ~/.ssh && chmod 700 ~/.ssh && echo '{escaped}' >> ~/.ssh/authorized_keys && chmod 600 ~/.ssh/authorized_keys";
    }
}
