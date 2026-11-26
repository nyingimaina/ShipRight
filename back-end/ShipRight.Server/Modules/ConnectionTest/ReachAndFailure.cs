using System.Net;
using System.Net.Sockets;
using Renci.SshNet.Common;

namespace ShipRight.Modules.ConnectionTest;

public enum TcpFailure { None, NameNotFound, Refused, TimedOut, Unreachable, ProxyUnavailable, ProxyBlocked, Other }

public sealed record TcpProbeResult(bool Reached, TcpFailure Failure)
{
    public static readonly TcpProbeResult Success = new(true, TcpFailure.None);
    public static TcpProbeResult Failed(TcpFailure kind) => new(false, kind);
}

/// <summary>Opens a plain network connection to the SSH port, so "cannot find / cannot reach" is told apart from "cannot log in".</summary>
public interface ITcpProbe
{
    Task<TcpProbeResult> ProbeAsync(string host, int port, TimeSpan timeout, CancellationToken ct);
}

public sealed class TcpProbe : ITcpProbe
{
    public async Task<TcpProbeResult> ProbeAsync(string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, limit.Token);
            return TcpProbeResult.Success;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return TcpProbeResult.Failed(TcpFailure.TimedOut);
        }
        catch (SocketException ex) { return TcpProbeResult.Failed(Kind(ex)); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return TcpProbeResult.Failed(TcpFailure.Other); }
    }

    public static TcpFailure Kind(SocketException ex) => ex.SocketErrorCode switch
    {
        SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData => TcpFailure.NameNotFound,
        SocketError.ConnectionRefused => TcpFailure.Refused,
        SocketError.TimedOut => TcpFailure.TimedOut,
        SocketError.HostUnreachable or SocketError.NetworkUnreachable => TcpFailure.Unreachable,
        _ => TcpFailure.Other,
    };
}

/// <summary>
/// Words for "can this computer reach the server at all". A Tailscale-style address only changes the wording of the
/// advice; nothing here depends on Tailscale being installed.
/// </summary>
public static class ReachAdvisor
{
    public static bool LooksLikeTailnet(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        var h = host.Trim();
        if (h.EndsWith(".ts.net", StringComparison.OrdinalIgnoreCase)) return true;
        if (!IPAddress.TryParse(h, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        return b[0] == 100 && b[1] is >= 64 and <= 127;
    }

    public static CheckItem Evaluate(string host, TcpProbeResult probe)
    {
        const string id = CheckIds.Reach;
        var title = CheckIds.Title(id);
        if (probe.Reached) return new(id, title, CheckStatus.Pass, "ShipRight can reach the server.");
        var tail = LooksLikeTailnet(host);
        return probe.Failure switch
        {
            TcpFailure.NameNotFound when tail => new(id, title, CheckStatus.Fail,
                "ShipRight could not find that Tailscale name.",
                "On this computer, make sure Tailscale is running and signed in, then run 'tailscale status' and check the server is listed. You can also use its 100.x address instead of the name."),
            TcpFailure.NameNotFound => new(id, title, CheckStatus.Fail,
                "ShipRight could not find a server with that address.",
                "Check the host for typos. Use a public IP address, or a name that this computer can look up."),
            TcpFailure.Refused => new(id, title, CheckStatus.Fail,
                "The server answered, but nothing is accepting SSH logins on port 22.",
                "Check that the SSH service is running on the server (sudo systemctl status ssh) and that it listens on port 22."),
            TcpFailure.TimedOut when tail => new(id, title, CheckStatus.Fail,
                "The server did not answer within 10 seconds.",
                "Make sure Tailscale is connected on this computer and on the server (run 'tailscale status' on both), and that the server is switched on."),
            TcpFailure.TimedOut => new(id, title, CheckStatus.Fail,
                "The server did not answer within 10 seconds.",
                "Check the server is switched on, the address is right, and that a firewall (for example a cloud security group) allows SSH (port 22) from this computer."),
            TcpFailure.Unreachable => new(id, title, CheckStatus.Fail,
                "There is no network route from this computer to that address.",
                tail ? "Make sure Tailscale is connected on this computer." : "Check this computer's internet connection and that the address is right."),
            TcpFailure.ProxyUnavailable => new(id, title, CheckStatus.Fail,
                "The server's Tailscale network is not connected, so ShipRight could not reach it.",
                "Open Tailscale in ShipRight, start this server's network, then test again. If it is already started, check that the server is signed in to the same tailnet."),
            TcpFailure.ProxyBlocked => new(id, title, CheckStatus.Fail,
                "Tailscale is connected, but it blocked the connection to this server.",
                "This is almost always the tailnet ACL: tag the server 'tag:servers', then allow 'tag:shipright' to reach 'tag:servers' on tcp:22. Alternatively, change the grant's destination to '*' (still tcp:22). Check the server is online in Tailscale, then test again."),
            _ => new(id, title, CheckStatus.Fail,
                "ShipRight could not open a connection to the server.",
                "Check the address and your network, then test again."),
        };
    }
}

/// <summary>Turns an exception from the SSH connection into a checklist line, without ever naming the key file's location.</summary>
public static class SshFailureClassifier
{
    public static CheckItem Classify(Exception ex, string host, string user)
    {
        var login = CheckIds.Login;
        var title = CheckIds.Title(login);
        switch (ex)
        {
            case SshAuthenticationException:
                return new(login, title, CheckStatus.Fail,
                    $"The server refused the key for user '{user}'. Either the key has not been added to this server yet, or the username is wrong.",
                    $"Go back to the step that gives ShipRight access and add the key to ~/.ssh/authorized_keys for '{user}'. Also check the username is spelled exactly as on the server.");
            case FileNotFoundException or DirectoryNotFoundException:
                return new(login, title, CheckStatus.Fail,
                    "ShipRight cannot find the key file for this server.",
                    "Generate a new key in this server's settings, then add it to the server.");
            case SshPassPhraseNullOrEmptyException:
                return new(login, title, CheckStatus.Fail,
                    "The key file is protected by a passphrase, which ShipRight cannot use.",
                    "Generate a new key in this server's settings (those keys have no passphrase) and add it to the server.");
            case SocketException se:
                return ReachAdvisor.Evaluate(host, TcpProbeResult.Failed(TcpProbe.Kind(se)));
            case SshConnectionException or SshOperationTimeoutException or TimeoutException:
                return new(CheckIds.Reach, CheckIds.Title(CheckIds.Reach), CheckStatus.Fail,
                    "The connection was cut off or timed out while starting SSH.",
                    "Try again. If it keeps happening, check that the server's SSH service is healthy and that the network between you and the server is stable.");
            default:
                return new(login, title, CheckStatus.Fail, $"Could not log in: {ex.Message}",
                    "Check the username and that the key has been added to the server, then test again.");
        }
    }
}
