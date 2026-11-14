using System.Security.Cryptography;
using Renci.SshNet;
using Serilog;
using ShipRight.Modules.Servers;
using ShipRight.Shared.SshRunner;

namespace ShipRight.Modules.ConnectionTest;

public sealed record HostKeyScan(string Algorithm, byte[] Blob);

/// <summary>Reads a server's host key without logging in and without trusting it.</summary>
public interface IHostKeyScanner
{
    Task<HostKeyScan> ScanAsync(string host, int port, CancellationToken ct);
}

/// <summary>The accepted host fingerprints, in the form the SSH runner keeps them.</summary>
public interface IKnownHosts
{
    string? Stored(string host);
    void Trust(string host, string fingerprint);
}

public sealed class KnownHostsAdapter(KnownHostsStore store) : IKnownHosts
{
    public string? Stored(string host) => store.GetStoredFingerprint(host);
    public void Trust(string host, string fingerprint) => store.Add(host, fingerprint);
}

public static class HostKeyFingerprint
{
    /// <summary>The format <c>ssh-keygen -lf</c> prints (base64 without padding).</summary>
    public static string Sha256(byte[] blob) => "SHA256:" + Convert.ToBase64String(SHA256.HashData(blob)).TrimEnd('=');

    /// <summary>Lower-case, colon-separated: the format the SSH runner stores.</summary>
    public static string Md5(byte[] blob) => BitConverter.ToString(MD5.HashData(blob)).Replace("-", ":").ToLowerInvariant();

    /// <summary>A read-only command the user can run on the server to see the fingerprint it reports about itself.</summary>
    public static string VerifyCommand(string algorithm)
    {
        var kind = algorithm.StartsWith("ssh-ed25519", StringComparison.Ordinal) ? "ed25519"
            : algorithm.Contains("rsa", StringComparison.Ordinal) ? "rsa"
            : algorithm.StartsWith("ecdsa", StringComparison.Ordinal) ? "ecdsa"
            : "*";
        return $"ssh-keygen -lf /etc/ssh/ssh_host_{kind}_key.pub";
    }
}

/// <summary>Connects only far enough to receive the server's host key, then refuses it, so nothing is trusted or logged in.</summary>
public sealed class SshHostKeyScanner : IHostKeyScanner
{
    public Task<HostKeyScan> ScanAsync(string host, int port, CancellationToken ct) =>
        Task.Run(() =>
        {
            HostKeyScan? found = null;
            using var client = new SshClient(host, port, "hostkey-check", "x");
            client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(15);
            client.HostKeyReceived += (_, e) =>
            {
                found = new HostKeyScan(e.HostKeyName, e.HostKey);
                e.CanTrust = false;
            };
            try { client.Connect(); }
            catch (Exception) when (found is not null) { /* expected: we refused the key on purpose */ }
            return found ?? throw new IOException("The server did not present a host key.");
        }, ct);
}

public sealed record TrustHostKeyRequest(string Sha256, bool AcknowledgeChange);

/// <summary>
/// GET shows the server's fingerprint and whether it is new, already trusted, or different from the one trusted before.
/// POST trusts it, but only the fingerprint that was just shown (the server is scanned again and must still match), and
/// only with an explicit acknowledgement when it replaces a different one. The host always comes from the stored server.
/// </summary>
public static class HostKeyRoutes
{
    public static void MapHostKeyRoutes(this WebApplication app)
    {
        app.MapGet("/api/servers/{serverId}/host-key",
            (string serverId, IServerStore store, KnownHostsStore known, CancellationToken ct) =>
                HandleScanAsync(serverId, store, new SshHostKeyScanner(), new KnownHostsAdapter(known), ct));

        app.MapPost("/api/servers/{serverId}/host-key/trust",
            (string serverId, TrustHostKeyRequest req, IServerStore store, KnownHostsStore known, CancellationToken ct) =>
                HandleTrustAsync(serverId, req, store, new SshHostKeyScanner(), new KnownHostsAdapter(known), ct));
    }

    private const int SshPort = 22;

    internal static async Task<IResult> HandleScanAsync(string serverId, IServerStore store, IHostKeyScanner scanner, IKnownHosts known, CancellationToken ct)
    {
        var server = await store.GetByIdAsync(serverId);
        if (server is null) return NotFound(serverId);
        try
        {
            var scan = await scanner.ScanAsync(server.Host.Trim(), SshPort, ct);
            var stored = known.Stored(server.Host.Trim());
            var md5 = HostKeyFingerprint.Md5(scan.Blob);
            var state = stored is null ? "new" : stored == md5 ? "trusted" : "changed";
            return Results.Ok(new
            {
                sha256 = HostKeyFingerprint.Sha256(scan.Blob),
                algorithm = scan.Algorithm,
                state,
                verifyCommand = HostKeyFingerprint.VerifyCommand(scan.Algorithm),
                message = state switch
                {
                    "trusted" => "ShipRight already trusts this server's identity.",
                    "changed" => "This server's identity is different from the one ShipRight trusted before. That happens after a rebuild or reinstall, but it can also mean someone is intercepting the connection. Only continue if you know why it changed.",
                    _ => "This is the first time ShipRight connects to this server. Compare this fingerprint with the one the server reports about itself.",
                },
            });
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warning(ex, "Host key scan failed for server {ServerId}", serverId);
            return Results.Json(new { isError = true, message = "ShipRight could not read the server's identity. Check that the server is reachable and that SSH is running, then try again." }, statusCode: 502);
        }
    }

    internal static async Task<IResult> HandleTrustAsync(string serverId, TrustHostKeyRequest req, IServerStore store, IHostKeyScanner scanner, IKnownHosts known, CancellationToken ct)
    {
        var server = await store.GetByIdAsync(serverId);
        if (server is null) return NotFound(serverId);
        HostKeyScan scan;
        try { scan = await scanner.ScanAsync(server.Host.Trim(), SshPort, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warning(ex, "Host key scan failed while trusting for server {ServerId}", serverId);
            return Results.Json(new { isError = true, message = "ShipRight could not read the server's identity, so nothing was trusted. Try again." }, statusCode: 502);
        }

        if (req.Sha256 != HostKeyFingerprint.Sha256(scan.Blob))
            return Results.Json(new { isError = true, message = "The server's identity is not the one that was shown. Nothing was trusted. Look at the fingerprint again." }, statusCode: 409);

        var md5 = HostKeyFingerprint.Md5(scan.Blob);
        var stored = known.Stored(server.Host.Trim());
        if (stored is not null && stored != md5 && !req.AcknowledgeChange)
            return Results.BadRequest(new { isError = true, message = "This replaces a different identity that was trusted before. Confirm that you expect the change." });

        known.Trust(server.Host.Trim(), md5);
        Log.Information("Host key trusted for server {ServerId}", serverId);
        return Results.Ok(new { trusted = true });
    }

    private static IResult NotFound(string id) => Results.NotFound(new { isError = true, message = $"Server '{id}' not found." });
}
