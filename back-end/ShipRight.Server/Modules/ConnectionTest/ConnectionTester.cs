using ShipRight.Modules.Projects;
using ShipRight.Shared.SshRunner;

namespace ShipRight.Modules.ConnectionTest;

/// <summary>
/// Tests a saved server in stages: can this computer reach it, can ShipRight log in, and what does the server have
/// (sudo, Docker, Compose, git, disk, deploy folder). The connection details always come from the stored server.
/// A stage that cannot run because an earlier one failed is "skipped", not guessed. Nothing on the server changes.
/// </summary>
public sealed class ConnectionTester(ISshRunner ssh, ITcpProbe tcp)
{
    private const int SshPort = 22;   // the SSH runner always connects to the standard port
    private static readonly TimeSpan ReachTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(60);

    public async Task<ConnectionTestResult> TestAsync(ServerConfig server, CancellationToken ct = default)
    {
        var host = (server.Host ?? "").Trim();
        if (host.Length == 0)
            return Stop(Fail(CheckIds.Reach, "No host has been entered for this server.", "Enter the server's address (an IP, or a name such as its Tailscale name)."),
                [CheckIds.Login, .. CheckIds.AfterLogin], "Checked after the server can be reached.");

        var reach = ReachAdvisor.Evaluate(host, await tcp.ProbeAsync(host, SshPort, ReachTimeout, ct));
        if (reach.Status == CheckStatus.Fail) return Stop(reach, [CheckIds.Login, .. CheckIds.AfterLogin], "Checked after the server can be reached.");

        if (string.IsNullOrWhiteSpace(server.SshKeyPath))
            return Result(reach,
                Fail(CheckIds.Login, "ShipRight does not have a key for this server yet.", "Generate a key in this server's settings, then add it to the server."),
                CheckIds.AfterLogin);

        string command;
        CheckItem? folderProblem = null;
        try { command = ServerProbe.Command(server.RemoteWorkingDir); }
        catch (ArgumentException)
        {
            folderProblem = Fail(CheckIds.Folder, "The deploy folder must be an absolute path (starting with /) with no '..' in it.", "Enter a full path such as /home/ubuntu/shop.");
            command = ServerProbe.Command(null);
        }

        var lines = new List<string>();
        int exit;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(ProbeTimeout);
        try
        {
            Task Collect(string l) { lock (lines) lines.Add(l); return Task.CompletedTask; }
            exit = await ssh.RunAsync(host, server.Username, server.SshKeyPath, command, Collect, Collect, limit.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            return Stop(new CheckItem(CheckIds.Reach, CheckIds.Title(CheckIds.Reach), CheckStatus.Fail,
                "The server took too long to answer after connecting.", "Try again. If it keeps happening, check the server is not overloaded."),
                [CheckIds.Login, .. CheckIds.AfterLogin], "Checked after the server can be reached.");
        }
        catch (Exception ex)
        {
            var item = Scrub(SshFailureClassifier.Classify(ex, host, server.Username), server.SshKeyPath);
            return item.Id == CheckIds.Reach
                ? Stop(item, [CheckIds.Login, .. CheckIds.AfterLogin], "Checked after the server can be reached.")
                : Result(reach, item, CheckIds.AfterLogin);
        }

        var login = new CheckItem(CheckIds.Login, CheckIds.Title(CheckIds.Login), CheckStatus.Pass, $"ShipRight logged in as '{server.Username}'.");
        if (exit != 0)
        {
            string tail;
            lock (lines) tail = string.Join(" | ", lines.Select(l => l.Trim()).Where(l => l.Length > 0).TakeLast(2));
            var items = new List<CheckItem> { reach, login };
            items.AddRange(CheckIds.AfterLogin.Select(id => new CheckItem(id, CheckIds.Title(id), CheckStatus.Warn,
                "The server's details could not be read" + (tail.Length > 0 ? $" ({tail})" : "") + ".", "Test again.")));
            return new ConnectionTestResult(items);
        }

        IReadOnlyList<CheckItem> advice;
        lock (lines) advice = ConnectionAdvisor.Evaluate(ServerProbe.Parse(lines), server.RemoteWorkingDir);
        var all = new List<CheckItem> { reach, login };
        all.AddRange(advice.Select(a => folderProblem is not null && a.Id == CheckIds.Folder ? folderProblem : a));
        return new ConnectionTestResult(all);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static CheckItem Fail(string id, string message, string fix) => new(id, CheckIds.Title(id), CheckStatus.Fail, message, fix);

    private static CheckItem Skipped(string id, string why) => new(id, CheckIds.Title(id), CheckStatus.Skipped, why);

    /// <summary>One failed or absent line, then every later line skipped.</summary>
    private static ConnectionTestResult Stop(CheckItem first, IEnumerable<string> skipped, string why = "Checked after the server can be reached.")
    {
        var items = new List<CheckItem> { first };
        if (first.Id != CheckIds.Reach) items.Insert(0, new CheckItem(CheckIds.Reach, CheckIds.Title(CheckIds.Reach), CheckStatus.Skipped, why));
        items.AddRange(skipped.Where(id => id != first.Id).Select(id => Skipped(id, why)));
        return new ConnectionTestResult(items);
    }

    /// <summary>Reachable, then a login problem, so the rest cannot be checked.</summary>
    private static ConnectionTestResult Result(CheckItem reach, CheckItem login, IEnumerable<string> skipped) =>
        new([reach, login, .. skipped.Select(id => Skipped(id, "Checked after ShipRight can log in."))]);

    /// <summary>The location of the private key is never shown, even if a library put it in a message.</summary>
    private static CheckItem Scrub(CheckItem item, string keyPath) =>
        string.IsNullOrWhiteSpace(keyPath)
            ? item
            : item with
            {
                Message = item.Message.Replace(keyPath, "(key file)"),
                Fix = item.Fix?.Replace(keyPath, "(key file)"),
            };
}
