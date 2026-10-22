using System.Text;
using Serilog;
using ShipRight.Modules.Projects;
using ShipRight.Shared.SshRunner;

namespace ShipRight.Modules.RemoteHost;

public sealed record CheckpointLogResult(bool Success, string Content, string? Error = null);

/// <summary>Reads ~/.shipright/checkpoints.log from a server so it can be downloaded from the UI.</summary>
public sealed class RemoteCheckpointLogReader
{
    private const string ReadCommand = "cat \"$HOME/.shipright/checkpoints.log\" 2>/dev/null || true";
    private readonly ISshRunner _ssh;

    public RemoteCheckpointLogReader(ISshRunner ssh) => _ssh = ssh;

    public async Task<CheckpointLogResult> ReadAsync(ServerConfig server, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        Task Append(string l) { lock (sb) sb.Append(l).Append('\n'); return Task.CompletedTask; }

        try
        {
            var exit = await _ssh.RunAsync(server.Host, server.Username, server.SshKeyPath, ReadCommand, Append, Append, ct);
            if (exit != 0)
                return new(false, string.Empty, $"Could not read the server log (exit {exit}): {sb.ToString().Trim()}");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Reading checkpoint log failed for server {ServerId}", server.Id);
            return new(false, string.Empty, $"Could not read the server log: {ex.Message}");
        }

        var text = sb.ToString();
        return new(true, text.Length == 0 ? "No checkpoints recorded yet. Run Prepare or Deploy first.\n" : text);
    }
}
