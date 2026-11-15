using ShipRight.Modules.Projects;

namespace ShipRight.Modules.ConnectionTest;

/// <summary>
/// Wraps <see cref="ConnectionTester"/>. When the SSH login fails and the server answers on port 22 with the Tailscale SSH
/// banner, the failure is explained properly: Tailscale SSH never uses ShipRight's key, it waits for Tailscale's own approval,
/// so the login just times out. Any other result passes through untouched.
/// </summary>
public sealed class TailscaleSshAwareTester(ConnectionTester inner, Func<string, CancellationToken, Task<string>>? readBanner = null)
{
    private readonly Func<string, CancellationToken, Task<string>> _banner =
        readBanner ?? ((host, ct) => NetDiagnostics.ReadSshBannerAsync(host, 22, TimeSpan.FromSeconds(4), ct));

    public static bool IsTailscaleBanner(string? banner) =>
        banner is not null && banner.Contains("SSH-2.0-Tailscale", StringComparison.OrdinalIgnoreCase);

    public async Task<ConnectionTestResult> TestAsync(ServerConfig server, CancellationToken ct = default)
    {
        var result = await inner.TestAsync(server, ct);
        var failed = result.Items.FirstOrDefault(i => i.Status == CheckStatus.Fail && i.Id is CheckIds.Reach or CheckIds.Login);
        if (failed is null) return result;

        string banner;
        try { banner = await _banner((server.Host ?? "").Trim(), ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return result; }
        if (!IsTailscaleBanner(banner)) return result;

        var advice = new CheckItem(failed.Id, failed.Title, CheckStatus.Fail,
            "This server answers with Tailscale SSH instead of regular SSH, so ShipRight's key is never used. The login waits for Tailscale to approve it and times out.",
            "Easiest: turn Tailscale SSH off on the server with the command below, then test again (regular SSH keys are used instead, so make sure your own key is in ~/.ssh/authorized_keys first). Or, in your Tailscale access rules, change this server's SSH rule from \"check\" to \"accept\".",
            "sudo tailscale set --ssh=false");
        return new ConnectionTestResult(result.Items.Select(i => ReferenceEquals(i, failed) ? advice : i).ToList());
    }
}
