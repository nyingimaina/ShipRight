using System.Text;
using Renci.SshNet;
using Serilog;
using ShipRight.Modules.Database.Providers;
using ShipRight.Modules.Projects;
using ShipRight.Shared.Events;
using ShipRight.Shared.SshRunner;
using ShipRight.Shared.Store;

namespace ShipRight.Modules.Database;

public record BackupFileInfo(string FileName, string FilePath, long SizeBytes, DateTime CreatedAt);

public class DatabaseOrchestrator
{
    private readonly IDbProviderResolver _resolver;
    private readonly ISshRunner _ssh;
    private readonly KnownHostsStore _knownHosts;
    private readonly BuildEventBus _bus;

    public DatabaseOrchestrator(
        IDbProviderResolver resolver,
        ISshRunner ssh,
        KnownHostsStore knownHosts,
        BuildEventBus bus)
    {
        _resolver  = resolver;
        _ssh       = ssh;
        _knownHosts = knownHosts;
        _bus       = bus;
    }

    // ── Backup ─────────────────────────────────────────────────────────────────

    public async Task BackupAsync(ProjectConfig project, string opId, CancellationToken ct)
    {
        var cfg      = project.Database!;
        var provider = _resolver.Resolve(cfg.Provider);
        var server   = project.Server;
        var backupDir = BackupDirFor(project.Id);
        Directory.CreateDirectory(backupDir);

        try
        {
            await EmitLog(opId, $"Starting {provider.ProviderName} backup of {cfg.DatabaseName}…");

            var timestamp  = DateTime.UtcNow.ToString("yyyy-MM-dd_HH-mm-ss");
            var localFile  = Path.Combine(backupDir, $"{cfg.DatabaseName}_{timestamp}{provider.BackupExtension}");

            if (provider.Transfer == BackupTransfer.StreamStdout)
            {
                var content = new global::System.Text.StringBuilder();
                var cmd = provider.BackupCommand(cfg);
                await EmitLog(opId, $"Running: {cmd}");

                var exit = await _ssh.RunAsync(server.Host, server.Username, server.SshKeyPath, cmd,
                    async line =>
                    {
                        content.Append(line).Append('\n');
                        await EmitLog(opId, line);
                    }, ct);

                if (exit != 0)
                    throw new InvalidOperationException($"Backup command exited with code {exit}.");

                await File.WriteAllTextAsync(localFile, content.ToString(), ct);
            }
            else // SftpFile
            {
                var remoteFile = provider.BackupFilePath(cfg, opId);
                var cmd = provider.BackupCommand(cfg).Replace("{{opId}}", opId);
                await EmitLog(opId, $"Running: {cmd}");

                var exit = await _ssh.RunAsync(server.Host, server.Username, server.SshKeyPath, cmd,
                    line => EmitLog(opId, line), ct);
                if (exit != 0)
                    throw new InvalidOperationException($"Backup command exited with code {exit}.");

                await EmitLog(opId, $"Downloading {remoteFile}…");
                await SftpDownloadAsync(server, remoteFile, localFile, ct);

                await EmitLog(opId, "Cleaning up remote temp file…");
                await _ssh.RunAsync(server.Host, server.Username, server.SshKeyPath,
                    provider.CleanupCommand(remoteFile), ct: ct);
            }

            await EmitLog(opId, $"Backup saved: {Path.GetFileName(localFile)}");
            PruneOldBackups(backupDir, cfg.DatabaseName, provider.BackupExtension, cfg.BackupRetainCount);

            await _bus.EmitAsync(opId, "complete", new { status = "success", fileName = Path.GetFileName(localFile) });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Backup failed for project {ProjectId}", project.Id);
            await _bus.EmitAsync(opId, "error", new { message = ex.Message });
        }
        finally
        {
            _bus.Complete(opId);
        }
    }

    // ── Restore ────────────────────────────────────────────────────────────────

    public async Task RestoreAsync(ProjectConfig project, string opId, string localBackupPath, CancellationToken ct)
    {
        var cfg      = project.Database!;
        var provider = _resolver.Resolve(cfg.Provider);
        var server   = project.Server;

        try
        {
            await EmitLog(opId, $"Restoring {cfg.DatabaseName} from {Path.GetFileName(localBackupPath)}…");

            var remoteFile = $"/tmp/shipright-restore-{opId}{provider.BackupExtension}";
            await EmitLog(opId, $"Uploading backup to {server.Host}:{remoteFile}…");
            await SftpUploadAsync(server, localBackupPath, remoteFile, ct);

            var cmd = provider.RestoreCommand(cfg, remoteFile);
            await EmitLog(opId, $"Running restore command…");
            var exit = await _ssh.RunAsync(server.Host, server.Username, server.SshKeyPath, cmd,
                line => EmitLog(opId, line), ct);

            await _ssh.RunAsync(server.Host, server.Username, server.SshKeyPath,
                provider.CleanupCommand(remoteFile), ct: ct);

            if (exit != 0)
                throw new InvalidOperationException($"Restore command exited with code {exit}.");

            await EmitLog(opId, "Restore complete.");
            await _bus.EmitAsync(opId, "complete", new { status = "success" });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Restore failed for project {ProjectId}", project.Id);
            await _bus.EmitAsync(opId, "error", new { message = ex.Message });
        }
        finally
        {
            _bus.Complete(opId);
        }
    }

    // ── Query ──────────────────────────────────────────────────────────────────

    public async Task QueryAsync(ProjectConfig project, string opId, string localSqlPath, CancellationToken ct)
    {
        var cfg      = project.Database!;
        var provider = _resolver.Resolve(cfg.Provider);
        var server   = project.Server;

        try
        {
            await EmitLog(opId, $"Running query against {cfg.DatabaseName}…");

            var remoteFile = $"/tmp/shipright-query-{opId}.sql";
            await EmitLog(opId, $"Uploading SQL file to {server.Host}:{remoteFile}…");
            await SftpUploadAsync(server, localSqlPath, remoteFile, ct);

            var cmd = provider.QueryCommand(cfg, remoteFile);
            await EmitLog(opId, "Executing query…");
            var exit = await _ssh.RunAsync(server.Host, server.Username, server.SshKeyPath, cmd,
                line => EmitLog(opId, line), ct);

            await _ssh.RunAsync(server.Host, server.Username, server.SshKeyPath,
                provider.CleanupCommand(remoteFile), ct: ct);

            if (exit != 0)
                throw new InvalidOperationException($"Query command exited with code {exit}.");

            await EmitLog(opId, "Query complete.");
            await _bus.EmitAsync(opId, "complete", new { status = "success" });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Query failed for project {ProjectId}", project.Id);
            await _bus.EmitAsync(opId, "error", new { message = ex.Message });
        }
        finally
        {
            _bus.Complete(opId);
        }
    }

    // ── Discovery ──────────────────────────────────────────────────────────────

    public async Task<List<object>> ListContainersAsync(ProjectConfig project)
    {
        var server = project.Server;
        var lines  = new List<string>();

        await _ssh.RunAsync(server.Host, server.Username, server.SshKeyPath,
            "docker ps --format \"{{.Names}}\\t{{.Image}}\\t{{.Status}}\"",
            line => { lines.Add(line); return Task.CompletedTask; });

        return lines
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l =>
            {
                var parts = l.Split('\t');
                return (object)new
                {
                    name   = parts.ElementAtOrDefault(0) ?? l,
                    image  = parts.ElementAtOrDefault(1) ?? "",
                    status = parts.ElementAtOrDefault(2) ?? ""
                };
            })
            .ToList();
    }

    public async Task<List<string>> ListDatabasesAsync(ProjectConfig project, string containerName, DbProviderType providerType)
    {
        var cfg = new DatabaseConfig
        {
            Provider      = providerType,
            ContainerName = containerName,
            RootUser      = providerType == DbProviderType.MariaDb ? "root" : "sa"
        };
        var provider = _resolver.Resolve(providerType);
        var server   = project.Server;
        var lines    = new List<string>();

        await _ssh.RunAsync(server.Host, server.Username, server.SshKeyPath,
            provider.ListDatabasesCommand(cfg),
            line => { lines.Add(line); return Task.CompletedTask; });

        // MariaDB: output is "Database\ndb1\ndb2\n..." — skip header
        // SQL Server: output has rows with dashes and column headers
        return lines
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Where(l => !l.StartsWith("Database", StringComparison.OrdinalIgnoreCase))
            .Where(l => !l.StartsWith("---"))
            .Where(l => !l.StartsWith("name", StringComparison.OrdinalIgnoreCase))
            .Where(l => !l.Contains("row") || !l.Contains("affected"))
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();
    }

    // ── Backup listing ─────────────────────────────────────────────────────────

    public List<BackupFileInfo> ListBackups(string projectId)
    {
        var dir = BackupDirFor(projectId);
        if (!Directory.Exists(dir)) return [];

        return Directory.GetFiles(dir)
            .Select(f =>
            {
                var info = new FileInfo(f);
                return new BackupFileInfo(info.Name, f, info.Length, info.CreationTimeUtc);
            })
            .OrderByDescending(b => b.CreatedAt)
            .ToList();
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private Task EmitLog(string opId, string message)
        => _bus.EmitAsync(opId, "log", new { message });

    private static string BackupDirFor(string projectId)
        => Path.Combine(DataDirectory.Resolve(), "backups", projectId);

    private static void PruneOldBackups(string dir, string dbName, string ext, int retain)
    {
        var files = Directory.GetFiles(dir, $"{dbName}_*{ext}")
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.CreationTimeUtc)
            .Skip(retain)
            .ToList();
        foreach (var f in files)
        {
            try { f.Delete(); }
            catch { /* best-effort */ }
        }
    }

    private async Task SftpUploadAsync(ServerConfig server, string localPath, string remotePath, CancellationToken ct)
    {
        using var keyFile = await SshKeyLoader.LoadAsync(server.SshKeyPath);
        using var sftp    = new SftpClient(server.Host, server.Username, keyFile);
        sftp.ConnectionInfo.Timeout = TimeSpan.FromSeconds(30);
        sftp.HostKeyReceived += (_, e) =>
        {
            var fp = BitConverter.ToString(e.FingerPrint).Replace("-", ":").ToLowerInvariant();
            _knownHosts.Add(server.Host, fp);
            e.CanTrust = true;
        };
        sftp.Connect();
        try
        {
            using var fs = File.OpenRead(localPath);
            sftp.UploadFile(fs, remotePath);
        }
        finally { sftp.Disconnect(); }
    }

    private async Task SftpDownloadAsync(ServerConfig server, string remotePath, string localPath, CancellationToken ct)
    {
        using var keyFile = await SshKeyLoader.LoadAsync(server.SshKeyPath);
        using var sftp    = new SftpClient(server.Host, server.Username, keyFile);
        sftp.ConnectionInfo.Timeout = TimeSpan.FromSeconds(30);
        sftp.HostKeyReceived += (_, e) =>
        {
            var fp = BitConverter.ToString(e.FingerPrint).Replace("-", ":").ToLowerInvariant();
            _knownHosts.Add(server.Host, fp);
            e.CanTrust = true;
        };
        sftp.Connect();
        try
        {
            using var fs = File.Create(localPath);
            sftp.DownloadFile(remotePath, fs);
        }
        finally { sftp.Disconnect(); }
    }
}
