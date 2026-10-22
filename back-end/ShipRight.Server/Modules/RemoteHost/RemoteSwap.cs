using ShipRight.Modules.Projects;
using ShipRight.Shared.SshRunner;

namespace ShipRight.Modules.RemoteHost;

public sealed record SwapResult(bool Success, string Output);

/// <summary>Creates a persistent swap file on a server that has none. Small cloud servers need it to survive memory spikes.</summary>
public sealed class RemoteSwap
{
    private readonly ISshRunner _ssh;
    public RemoteSwap(ISshRunner ssh) => _ssh = ssh;

    public static string BuildCreateCommand(int sizeGb)
    {
        if (sizeGb is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(sizeGb), "Swap size must be 1–8 GB.");
        return "if [ \"$(swapon --show | wc -l)\" -gt 0 ]; then echo 'Swap is already enabled.'; free -m; exit 0; fi; " +
               $"need=$(( {sizeGb} * 1024 + 1024 )); avail=$(df -Pm / | awk 'NR==2{{print $4}}'); " +
               $"if [ \"$avail\" -lt \"$need\" ]; then echo 'Not enough free disk space for a {sizeGb}G swap file.'; exit 1; fi; " +
               $"sudo -n fallocate -l {sizeGb}G /swapfile || sudo -n dd if=/dev/zero of=/swapfile bs=1M count={sizeGb * 1024}; " +
               "sudo -n chmod 600 /swapfile && sudo -n mkswap /swapfile && sudo -n swapon /swapfile && " +
               "{ grep -q '^/swapfile' /etc/fstab || echo '/swapfile none swap sw 0 0' | sudo -n tee -a /etc/fstab >/dev/null; } && " +
               "echo 'Swap enabled.' && free -m";
    }

    public async Task<SwapResult> CreateAsync(ServerConfig server, int sizeGb, CancellationToken ct = default)
    {
        var lines = new List<string>();
        Task Capture(string l) { lock (lines) lines.Add(l); return Task.CompletedTask; }
        var exit = await _ssh.RunAsync(server.Host, server.Username, server.SshKeyPath,
            BuildCreateCommand(sizeGb), Capture, Capture, ct);
        string output; lock (lines) output = string.Join("\n", lines);
        return new(exit == 0, output);
    }
}
