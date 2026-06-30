using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Renci.SshNet;
using Serilog;
using ShipRight.Modules.Database.Providers;
using ShipRight.Modules.Projects;
using ShipRight.Shared.Events;
using ShipRight.Shared.SshRunner;
using ShipRight.Shared.Store;

namespace ShipRight.Modules.Database;

public record BackupFileInfo(string FileName, string FilePath, long SizeBytes, DateTime CreatedAt);
public record InferResult(DatabaseConfig Config, string[] Detected);

public class DatabaseOrchestrator
{
    private readonly IDbProviderResolver _resolver;
    private readonly ISshRunner _ssh;
    private readonly KnownHostsStore _knownHosts;
    private readonly BuildEventBus _bus;
    private readonly ConcurrentDictionary<string, string> _activeOps = new();

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

        _activeOps[project.Id] = opId;
        try
        {
            await EmitLog(opId, $"Starting {provider.ProviderName} backup of {cfg.DatabaseName}…");

            var timestamp  = DateTime.UtcNow.ToString("yyyy-MM-dd_HH-mm-ss");
            var localFile  = Path.Combine(backupDir, $"{cfg.DatabaseName}_{timestamp}{provider.BackupExtension}");

            if (provider.Transfer == BackupTransfer.StreamStdout)
            {
                var cmd = provider.BackupCommand(cfg);
                Log.Information("Backup command for {DB}: {Cmd}", cfg.DatabaseName, cmd);
                await EmitLog(opId, "Running backup command…");

                // Stream stdout directly to disk — avoids holding entire dump in RAM
                await using var writer = new StreamWriter(localFile, append: false, Encoding.UTF8);
                long lineCount = 0, lastReported = 0;

                var exit = await _ssh.RunAsync(
                    server.Host, server.Username, server.SshKeyPath, cmd,
                    onOutput: async line =>
                    {
                        await writer.WriteLineAsync(line);
                        lineCount++;
                        if (lineCount - lastReported >= 10_000)
                        {
                            lastReported = lineCount;
                            await EmitLog(opId, $"Streaming… {lineCount:N0} lines received");
                        }
                    },
                    onStderr: async line =>
                    {
                        Log.Warning("Backup stderr [{DB}]: {Line}", cfg.DatabaseName, line);
                        await EmitLog(opId, line);
                    },
                    ct: ct);

                await writer.FlushAsync(ct);

                if (exit != 0)
                    throw new InvalidOperationException($"Backup command exited with code {exit}.");
            }
            else // SftpFile
            {
                var remoteFile = provider.BackupFilePath(cfg, opId);
                var cmd = provider.BackupCommand(cfg).Replace("{{opId}}", opId);
                Log.Information("Backup command for {DB}: {Cmd}", cfg.DatabaseName, cmd);
                await EmitLog(opId, "Running backup command…");

                var exit = await _ssh.RunAsync(server.Host, server.Username, server.SshKeyPath, cmd,
                    line => EmitLog(opId, line), ct: ct);
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
            _activeOps.TryRemove(project.Id, out _);
            _bus.Complete(opId);
        }
    }

    public string? GetActiveOpId(string projectId) =>
        _activeOps.TryGetValue(projectId, out var id) ? id : null;

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
                line => EmitLog(opId, line), ct: ct);

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
            var exit = await _ssh.RunAsync(
                server.Host, server.Username, server.SshKeyPath, cmd,
                onOutput: line => EmitOutput(opId, line),
                onStderr: async line =>
                {
                    Log.Warning("Query stderr [{ProjectId}/{DB}]: {Line}", project.Id, cfg.DatabaseName, line);
                    await EmitLog(opId, line);
                },
                ct: ct);

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

    // ── Raw query (SQL typed inline) ───────────────────────────────────────────

    public async Task QueryRawAsync(ProjectConfig project, string opId, string sql, CancellationToken ct)
    {
        var tempDir  = Path.Combine(DataDirectory.Resolve(), "temp");
        Directory.CreateDirectory(tempDir);
        var tempFile = Path.Combine(tempDir, $"raw-{opId}.sql");
        try
        {
            await File.WriteAllTextAsync(tempFile, sql, ct);
            await QueryAsync(project, opId, tempFile, ct);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { /* best-effort */ }
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

    public async Task<List<string>> ListDatabasesAsync(ProjectConfig project, string containerName, DbProviderType providerType, string? rootUser = null, string? rootPassword = null)
    {
        var cfg = new DatabaseConfig
        {
            Provider      = providerType,
            ContainerName = containerName,
            RootUser      = rootUser ?? (providerType == DbProviderType.MariaDb ? "root" : "sa"),
            RootPassword  = rootPassword ?? string.Empty
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

    public void DeleteBackup(string projectId, string filePath)
    {
        var backupDir   = Path.GetFullPath(BackupDirFor(projectId));
        var resolvedPath = Path.GetFullPath(filePath);

        if (!resolvedPath.StartsWith(backupDir, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("File is not within the backup directory.");

        if (!File.Exists(resolvedPath))
            throw new InvalidOperationException($"Backup file not found: {Path.GetFileName(resolvedPath)}");

        File.Delete(resolvedPath);
        Log.Information("Backup deleted: {File}", Path.GetFileName(resolvedPath));
    }

    public string? ResolveBackupFile(string projectId, string fileName)
    {
        var backupDir    = Path.GetFullPath(BackupDirFor(projectId));
        var resolvedPath = Path.GetFullPath(Path.Combine(backupDir, fileName));
        if (!resolvedPath.StartsWith(backupDir, StringComparison.OrdinalIgnoreCase)) return null;
        return File.Exists(resolvedPath) ? resolvedPath : null;
    }

    // ── Infer from docker-compose ──────────────────────────────────────────────

    public async Task<InferResult?> InferConfigAsync(ProjectConfig project)
    {
        var workDir = project.Wsl?.WorkingDir;
        if (string.IsNullOrWhiteSpace(workDir)) return null;

        var composePath = workDir.TrimEnd('/') + "/docker-compose.yml";
        var content = await ReadWslFileAsync(composePath)
            ?? await ReadWslFileAsync(workDir.TrimEnd('/') + "/docker-compose.yaml");
        if (content is null) return null;

        return await InferFromContentAsync(content, project.Server);
    }

    private async Task<InferResult?> InferFromContentAsync(string content, ServerConfig? server)
    {
        var (provider, containerHint, databaseName, detected) = ParseDockerComposeForDatabase(content);
        if (!provider.HasValue) return null;

        var containerName = string.Empty;
        if (!string.IsNullOrWhiteSpace(server?.Host))
        {
            try
            {
                var containerLines = new List<string>();
                await _ssh.RunAsync(server.Host, server.Username, server.SshKeyPath,
                    "docker ps --format \"{{.Names}}\\t{{.Image}}\"",
                    line => { containerLines.Add(line); return Task.CompletedTask; });

                var dbKeyword = provider.Value == DbProviderType.MariaDb ? "mariadb" : "mssql";
                foreach (var rawLine in containerLines)
                {
                    if (string.IsNullOrWhiteSpace(rawLine)) continue;
                    var parts = rawLine.Split('\t');
                    var cName  = parts.ElementAtOrDefault(0) ?? "";
                    var cImage = parts.ElementAtOrDefault(1) ?? "";

                    var nameMatch  = !string.IsNullOrEmpty(containerHint)
                        && cName.Contains(containerHint, StringComparison.OrdinalIgnoreCase);
                    var imageMatch = cImage.Contains(dbKeyword, StringComparison.OrdinalIgnoreCase);

                    if (nameMatch || imageMatch)
                    {
                        containerName = cName;
                        detected.Add($"Running container matched: {cName} ({cImage})");
                        break;
                    }
                }
            }
            catch { /* SSH unreachable — return partial result */ }
        }

        var config = new DatabaseConfig
        {
            Provider        = provider.Value,
            ContainerName   = containerName,
            DatabaseName    = databaseName ?? string.Empty,
            RootUser        = provider.Value == DbProviderType.MariaDb ? "root" : "sa",
            BackupRetainCount = 10,
        };

        return new InferResult(config, detected.ToArray());
    }

    private static async Task<string?> ReadWslFileAsync(string linuxPath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "wsl",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("--");
            psi.ArgumentList.Add("cat");
            psi.ArgumentList.Add(linuxPath);

            using var proc = Process.Start(psi);
            if (proc is null) return null;
            var text = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();
            return proc.ExitCode == 0 && !string.IsNullOrWhiteSpace(text) ? text : null;
        }
        catch { return null; }
    }
    private static (DbProviderType? Provider, string? ContainerHint, string? DatabaseName, List<string> Detected)
        ParseDockerComposeForDatabase(string content)
    {
        var lines    = content.Split('\n');
        var detected = new List<string>();

        var dbImageKeywords = new Dictionary<string, DbProviderType>(StringComparer.OrdinalIgnoreCase)
        {
            ["mariadb"]                  = DbProviderType.MariaDb,
            ["mysql"]                    = DbProviderType.MariaDb,
            ["mssql"]                    = DbProviderType.SqlServer,
            ["mcr.microsoft.com/mssql"]  = DbProviderType.SqlServer,
        };

        // Find first line containing image: <db-keyword>
        int imgIdx = -1;
        DbProviderType? provider = null;

        for (int i = 0; i < lines.Length; i++)
        {
            var lower = lines[i].ToLowerInvariant();
            if (!lower.Contains("image:")) continue;

            foreach (var (kw, prov) in dbImageKeywords)
            {
                if (!lower.Contains(kw.ToLowerInvariant())) continue;

                imgIdx   = i;
                provider = prov;
                var colonPos = lines[i].IndexOf("image:", StringComparison.OrdinalIgnoreCase);
                var imgValue = lines[i][(colonPos + 6)..].Trim().Trim('"', '\'');
                detected.Add($"Detected {prov} image: {imgValue}");
                break;
            }
            if (imgIdx >= 0) break;
        }

        if (!provider.HasValue) return (null, null, null, detected);

        var imageIndent = lines[imgIdx].Length - lines[imgIdx].TrimStart().Length;

        // Find service name: look backward for first line at shallower indent ending with ':'
        string? serviceName = null;
        for (int i = imgIdx - 1; i >= 0; i--)
        {
            var l = lines[i];
            if (string.IsNullOrWhiteSpace(l)) continue;
            var indent = l.Length - l.TrimStart().Length;
            if (indent < imageIndent)
            {
                var trimmed = l.Trim();
                if (trimmed.EndsWith(':')) serviceName = trimmed.TrimEnd(':');
                break;
            }
        }

        // Scan surrounding context for container_name and env vars
        string? containerHint = null;
        string? databaseName  = null;

        int start = Math.Max(0, imgIdx - 5);
        int end   = Math.Min(lines.Length - 1, imgIdx + 60);

        for (int i = start; i <= end; i++)
        {
            var l = lines[i];
            if (string.IsNullOrWhiteSpace(l)) continue;

            // Stop if we hit a new sibling service block after the image line
            if (i > imgIdx + 2)
            {
                var indent = l.Length - l.TrimStart().Length;
                if (indent < imageIndent && !l.TrimStart().StartsWith('#')) break;
            }

            // container_name: value
            var cmMatch = Regex.Match(l, @"container_name:\s*(.+)", RegexOptions.IgnoreCase);
            if (cmMatch.Success)
            {
                containerHint = cmMatch.Groups[1].Value.Trim().Trim('"', '\'').Split('#')[0].Trim();
                continue;
            }

            // Dict style: KEY: value (at ≥4 spaces, all-uppercase key)
            var dictMatch = Regex.Match(l, @"^\s{4,}([A-Z][A-Z0-9_]*):\s+(.+)");
            if (dictMatch.Success)
            {
                ApplyEnvVar(dictMatch.Groups[1].Value, dictMatch.Groups[2].Value, ref databaseName);
                continue;
            }

            // List style: - KEY=value
            var listMatch = Regex.Match(l, @"^\s+-\s+([A-Z][A-Z0-9_]*)=(.+)");
            if (listMatch.Success)
                ApplyEnvVar(listMatch.Groups[1].Value, listMatch.Groups[2].Value, ref databaseName);
        }

        if (databaseName != null) detected.Add($"Database name: {databaseName}");
        var hint = containerHint ?? serviceName;
        if (hint != null) detected.Add($"Container hint: {hint}");

        return (provider, hint, databaseName, detected);
    }

    private static void ApplyEnvVar(string key, string rawValue, ref string? databaseName)
    {
        if (key is not ("MYSQL_DATABASE" or "MARIADB_DATABASE" or "MSSQL_DB" or "MSSQL_DATABASE"))
            return;
        var val = rawValue.Trim().Trim('"', '\'').Split('#')[0].Trim();
        // Skip env var placeholders like ${VAR_NAME}
        if (!val.StartsWith('$') && !string.IsNullOrEmpty(val))
            databaseName = val;
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    public virtual async Task<bool> ScheduledBackupAsync(ProjectConfig project, CancellationToken ct)
    {
        var cfg = project.Database!;
        var provider = _resolver.Resolve(cfg.Provider);
        var server = project.Server;
        var backupDir = BackupDirFor(project.Id);
        Directory.CreateDirectory(backupDir);

        try
        {
            var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd_HH-mm-ss");
            var localFile = Path.Combine(backupDir, $"{cfg.DatabaseName}_{timestamp}{provider.BackupExtension}");

            if (provider.Transfer == BackupTransfer.StreamStdout)
            {
                var cmd = provider.BackupCommand(cfg);
                Log.Information("Scheduled backup for {DB}: {Cmd}", cfg.DatabaseName, cmd);

                await using var writer = new StreamWriter(localFile, append: false, Encoding.UTF8);
                long lineCount = 0;

                var exit = await _ssh.RunAsync(
                    server.Host, server.Username, server.SshKeyPath, cmd,
                    onOutput: async line =>
                    {
                        await writer.WriteLineAsync(line);
                        lineCount++;
                    },
                    ct: ct);

                await writer.FlushAsync(ct);
                if (exit != 0)
                {
                    Log.Error("Scheduled backup exited with code {Exit} for {DB}", exit, cfg.DatabaseName);
                    return false;
                }
            }
            else
            {
                var opId = Guid.NewGuid().ToString("N");
                var remoteFile = provider.BackupFilePath(cfg, opId);
                var cmd = provider.BackupCommand(cfg).Replace("{{opId}}", opId);
                Log.Information("Scheduled backup for {DB}: {Cmd}", cfg.DatabaseName, cmd);

                var exit = await _ssh.RunAsync(server.Host, server.Username, server.SshKeyPath, cmd, ct: ct);
                if (exit != 0) return false;

                await SftpDownloadAsync(server, remoteFile, localFile, ct);
                await _ssh.RunAsync(server.Host, server.Username, server.SshKeyPath,
                    provider.CleanupCommand(remoteFile), ct: ct);
            }

            PruneOldBackups(backupDir, cfg.DatabaseName, provider.BackupExtension, cfg.BackupRetainCount);
            Log.Information("Scheduled backup complete for {DB}: {File}", cfg.DatabaseName, Path.GetFileName(localFile));
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Scheduled backup failed for project {ProjectId}", project.Id);
            return false;
        }
    }

    private Task EmitLog(string opId, string message)
        => _bus.EmitAsync(opId, "log", new { message });

    private Task EmitOutput(string opId, string line)
        => _bus.EmitAsync(opId, "output", new { line });

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
