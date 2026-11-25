using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Text.RegularExpressions;
using Serilog;
using ShipRight.Modules.Database.Providers;
using ShipRight.Modules.Projects;
using ShipRight.Modules.ServerNetwork;
using ShipRight.Shared.Events;
using ShipRight.Shared.SshRunner;
using ShipRight.Shared.Store;

namespace ShipRight.Modules.Database;

public record InferResult(DatabaseConfig Config, string[] Detected);

public class DatabaseOrchestrator
{
    private readonly IDbProviderResolver _resolver;
    private readonly ISshRunner _ssh;
    private readonly KnownHostsStore _knownHosts;
    private readonly BuildEventBus _bus;
    private readonly IServerNetworkResolver? _networks;
    private readonly ConcurrentDictionary<string, string> _activeOps = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _projectLocks = new(StringComparer.Ordinal);
    private readonly BackupStorage _storage;
    private readonly BackupKeyring _keyring;
    private readonly IRawSshRunner? _rawSsh;
    private readonly BackupRunTelemetry? _telemetry;

    public DatabaseOrchestrator(
        IDbProviderResolver resolver,
        ISshRunner ssh,
        KnownHostsStore knownHosts,
        BuildEventBus bus,
        IServerNetworkResolver? networks = null,
        IRawSshRunner? rawSsh = null,
        BackupStorage? storage = null,
        BackupKeyring? keyring = null,
        BackupRunTelemetry? telemetry = null)
    {
        _resolver  = resolver;
        _ssh       = ssh;
        _knownHosts = knownHosts;
        _bus       = bus;
        _networks  = networks;
        _rawSsh    = rawSsh;
        _storage   = storage ?? BackupStorage.Resolve();
        _keyring   = keyring ?? new BackupKeyring();
        _telemetry = telemetry;
    }

    // ── Backup ─────────────────────────────────────────────────────────────────

    public async Task BackupAsync(ProjectConfig project, string opId, CancellationToken ct)
    {
        var cfg      = project.Database!;
        var provider = _resolver.Resolve(cfg.Provider);

        _activeOps[project.Id] = opId;
        try
        {
            await EmitLog(opId, $"Starting {provider.ProviderName} backup of {cfg.DatabaseName}…");
            var meta = await RunBackupAsync(project, provider, BackupSource.Manual, opId, ct);
            await EmitLog(opId, $"Backup saved: {meta.FileName} ({ByteSize.Format(meta.StoredBytes)} stored, {ByteSize.Format(meta.OriginalBytes)} original)");
            await _bus.EmitAsync(opId, "complete", new
            {
                status = "success",
                fileName = meta.FileName,
                storedBytes = meta.StoredBytes,
                originalBytes = meta.OriginalBytes,
            });
        }
        catch (NetworkUnavailableException ex)
        {
            Log.Warning(ex, "Backup network unavailable for project {ProjectId}", project.Id);
            await _bus.EmitAsync(opId, "error", new { message = ex.Message });
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

    /// <summary>
    /// One path for manual and scheduled runs: read the dump as raw bytes, hash it, compress it, seal it,
    /// write the sidecar, then apply retention. The project lock keeps runs and restores from overlapping.
    /// </summary>
    private async Task<BackupArtifactMeta> RunBackupAsync(
        ProjectConfig project, IDbProvider provider, string source, string? opId, CancellationToken ct)
    {
        var cfg = project.Database!;
        using var lease = await AcquireAsync(project.Id, ct);

        var artifacts = BackupArchive.List(_storage, project.Id);
        var estimate = Math.Max((artifacts.FirstOrDefault()?.OriginalBytes ?? 0) * 2, 128L * 1024 * 1024);
        var free = _storage.AvailableBytes();
        if (free >= 0 && free < estimate)
            throw new InvalidOperationException(
                $"Not enough space for a backup: {ByteSize.Format(free)} available, about {ByteSize.Format(estimate)} needed.");

        var options = new BackupWriteOptions(
            project.Id,
            cfg.DatabaseName,
            provider.BackupExtension.TrimStart('.'),
            source,
            BackupCompression.Clamp(cfg.BackupCompressionLevel),
            cfg.BackupEncrypt);

        var meta = provider.Transfer == BackupTransfer.StreamStdout
            ? await WriteFromCommandAsync(project, provider, options, opId, ct)
            : await WriteFromRemoteFileAsync(project, provider, options, opId, ct);

        PruneExpired(project.Id, cfg, opId);
        return meta;
    }

    /// <summary>Streams the dump command's stdout straight into the archive; no text decoding, no line endings touched.</summary>
    private async Task<BackupArtifactMeta> WriteFromCommandAsync(
        ProjectConfig project, IDbProvider provider, BackupWriteOptions options, string? opId, CancellationToken ct)
    {
        if (_rawSsh is null)
            throw new InvalidOperationException("Raw SSH streaming is not available, so a byte-exact backup cannot be taken.");

        var cfg = project.Database!;
        var server = project.Server;
        var command = provider.BackupCommand(cfg);
        Log.Information("Backup command for {DB} (raw stream)", cfg.DatabaseName);

        var pipe = new Pipe(new PipeOptions(
            pauseWriterThreshold: 8L * 1024 * 1024,
            resumeWriterThreshold: 4L * 1024 * 1024,
            useSynchronizationContext: false));

        var writer = BackupArchive.WriteAsync(_storage, _keyring, options, pipe.Reader.AsStream(),
            opId is null ? null : bytes => EmitProgressBytes(opId, bytes), ct);

        int exit;
        try
        {
            exit = await _rawSsh.RunAsync(server.Host, server.Username, server.SshKeyPath, command,
                pipe.Writer.AsStream(),
                onStderr: line =>
                {
                    Log.Warning("Backup stderr [{DB}]: {Line}", cfg.DatabaseName, line);
                    return opId is null ? Task.CompletedTask : EmitLog(opId, line);
                },
                ct);
            await pipe.Writer.CompleteAsync();
        }
        catch (Exception ex)
        {
            await pipe.Writer.CompleteAsync(ex);
            try { await writer; } catch { /* the original failure is the useful one */ }
            throw;
        }

        var meta = await writer;
        if (exit != 0)
        {
            BackupArchive.Delete(_storage, project.Id, meta.FileName);
            throw new InvalidOperationException($"Backup command exited with code {exit}.");
        }

        return meta;
    }

    /// <summary>For providers that write a file on the server (SQL Server): pull it over SFTP into the same archive.</summary>
    private async Task<BackupArtifactMeta> WriteFromRemoteFileAsync(
        ProjectConfig project, IDbProvider provider, BackupWriteOptions options, string? opId, CancellationToken ct)
    {
        var cfg = project.Database!;
        var server = project.Server;
        var token = opId ?? Guid.NewGuid().ToString("N");
        var remoteFile = provider.BackupFilePath(cfg, token);
        var command = provider.BackupCommand(cfg).Replace("{{opId}}", token);
        Log.Information("Backup command for {DB}", cfg.DatabaseName);

        var exit = await _ssh.RunAsync(server.Host, server.Username, server.SshKeyPath, command,
            line => opId is null ? Task.CompletedTask : EmitLog(opId, line), ct: ct);
        if (exit != 0)
            throw new InvalidOperationException($"Backup command exited with code {exit}.");

        try
        {
            if (opId is not null) await EmitLog(opId, $"Downloading {remoteFile}…");

            var pipe = new Pipe(new PipeOptions(
                pauseWriterThreshold: 8L * 1024 * 1024,
                resumeWriterThreshold: 4L * 1024 * 1024,
                useSynchronizationContext: false));

            var writer = BackupArchive.WriteAsync(_storage, _keyring, options, pipe.Reader.AsStream(),
                opId is null ? null : bytes => EmitProgressBytes(opId, bytes), ct);

            try
            {
                await SftpDownloadToStreamAsync(server, remoteFile, pipe.Writer.AsStream(), ct);
                await pipe.Writer.CompleteAsync();
            }
            catch (Exception ex)
            {
                await pipe.Writer.CompleteAsync(ex);
                try { await writer; } catch { /* the download failure is the useful one */ }
                throw;
            }

            return await writer;
        }
        finally
        {
            try
            {
                await _ssh.RunAsync(server.Host, server.Username, server.SshKeyPath,
                    provider.CleanupCommand(remoteFile), ct: CancellationToken.None);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not clean up the remote backup file {File}", remoteFile);
            }
        }
    }

    private Task EmitProgressBytes(string opId, long bytes) =>
        EmitLog(opId, $"Streaming… {ByteSize.Format(bytes)} received");

    private void PruneExpired(string projectId, DatabaseConfig cfg, string? opId)
    {
        var policy = RetentionPolicy.From(cfg.BackupRetainCount, cfg.BackupRetainDays);
        var decision = BackupRetention.Plan(BackupArchive.List(_storage, projectId), DateTimeOffset.UtcNow, policy);
        foreach (var artifact in decision.Delete)
        {
            if (BackupArchive.Delete(_storage, projectId, artifact.FileName))
                Log.Information("Backup pruned: {File} (keep {Count}, age {Days})", artifact.FileName, policy.KeepCount, policy.KeepDays);
        }
    }

    private async Task<IDisposable> AcquireAsync(string projectId, CancellationToken ct)
    {
        var gate = _projectLocks.GetOrAdd(projectId, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(TimeSpan.Zero, ct))
            throw new InvalidOperationException("A backup or restore is already running for this project.");
        return new Releaser(gate);
    }

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }

    // ── Restore ────────────────────────────────────────────────────────────────

    public async Task RestoreAsync(ProjectConfig project, string opId, string fileName, CancellationToken ct)
    {
        var cfg      = project.Database!;
        var provider = _resolver.Resolve(cfg.Provider);
        var server   = project.Server;

        try
        {
            using var lease = await AcquireAsync(project.Id, ct);
            await EmitLog(opId, $"Restoring {cfg.DatabaseName} from {fileName}…");

            var (stream, meta) = await BackupArchive.OpenReadAsync(
                _storage, project.Id, fileName, _keyring, BackupReadMode.Plain, ct);
            await using (stream)
            {
                var remoteFile = $"/tmp/shipright-restore-{opId}{provider.BackupExtension}";
                await EmitLog(opId, $"Uploading {(meta.OriginalBytes > 0 ? ByteSize.Format(meta.OriginalBytes) : "backup")} to {server.Host}…");
                await SftpUploadFromStreamAsync(server, stream, remoteFile, ct);

                var cmd = provider.RestoreCommand(cfg, remoteFile);
                await EmitLog(opId, "Running restore command…");
                var exit = await _ssh.RunAsync(server.Host, server.Username, server.SshKeyPath, cmd,
                    line => EmitLog(opId, line), ct: ct);

                try
                {
                    await _ssh.RunAsync(server.Host, server.Username, server.SshKeyPath,
                        provider.CleanupCommand(remoteFile), ct: CancellationToken.None);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Could not remove the restore temp file {File}", remoteFile);
                }

                if (exit != 0)
                    throw new InvalidOperationException($"Restore command exited with code {exit}.");
            }

            await EmitLog(opId, "Restore complete.");
            await _bus.EmitAsync(opId, "complete", new { status = "success" });
        }
        catch (NetworkUnavailableException ex)
        {
            Log.Warning(ex, "Restore network unavailable for project {ProjectId}", project.Id);
            await _bus.EmitAsync(opId, "error", new { message = ex.Message });
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

    public async Task QueryAsync(ProjectConfig project, string opId, string localSqlPath, CancellationToken ct, string? displayName = null)
    {
        var cfg      = project.Database!;
        var provider = _resolver.Resolve(cfg.Provider);
        var server   = project.Server;

        try
        {
            var size = new FileInfo(localSqlPath).Length;
            await EmitLog(opId, $"Running {displayName ?? Path.GetFileName(localSqlPath)} ({ByteSize.Format(size)}) against {cfg.DatabaseName}…");

            var remoteFile = $"/tmp/shipright-query-{opId}.sql";
            await EmitLog(opId, $"Uploading to {server.Host}:{remoteFile}…");
            var upload = NewProgress(opId, "upload", size);
            await SftpUploadWithProgressAsync(server, localSqlPath, remoteFile, upload, ct);

            var cmd = QueryProgress.Wrap(provider.QueryCommand(cfg, remoteFile), remoteFile);
            await EmitLog(opId, "Executing query…");
            var run = NewProgress(opId, "run", size);
            var exit = await _ssh.RunAsync(
                server.Host, server.Username, server.SshKeyPath, cmd,
                onOutput: line => EmitOutput(opId, line),
                onStderr: async line =>
                {
                    if (QueryProgress.TryParse(line, out var readSoFar, out _)) { run.Report(readSoFar); return; }
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
            await QueryAsync(project, opId, tempFile, ct, displayName: "your SQL");
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

    public IReadOnlyList<BackupArtifactMeta> ListBackups(string projectId) =>
        BackupArchive.List(_storage, projectId);

    public bool DeleteBackup(string projectId, string fileName)
    {
        if (!BackupArchive.Delete(_storage, projectId, fileName))
            throw new InvalidOperationException($"Backup file not found: {fileName}");
        return true;
    }

    public string? ResolveBackupFile(string projectId, string fileName) =>
        _storage.ResolveArtifact(projectId, fileName);

    public Task<(Stream Stream, BackupArtifactMeta Meta)> OpenBackupAsync(
        string projectId, string fileName, BackupReadMode mode, CancellationToken ct = default) =>
        BackupArchive.OpenReadAsync(_storage, projectId, fileName, _keyring, mode, ct);

    public BackupStorageInfo StorageInfo() => BackupArchive.Info(_storage);

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
        var provider = _resolver.Resolve(project.Database!.Provider);
        var started = Stopwatch.GetTimestamp();
        try
        {
            var meta = await RunBackupAsync(project, provider, BackupSource.Schedule, null, ct);
            _telemetry?.Record(project.Id, new BackupRunInfo(
                meta.FileName, meta.OriginalBytes, meta.StoredBytes,
                (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds));
            Log.Information("Scheduled backup complete for {DB}: {File}", project.Database.DatabaseName, meta.FileName);
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

    private Task EmitProgress(string opId, string stage, string message, int percent, long done, long total)
    {
        _bus.EmitAsync(opId, "progress", new { stage, percent, doneBytes = done, totalBytes = total }).GetAwaiter().GetResult();
        return EmitLog(opId, message);
    }

    private ProgressReporter NewProgress(string opId, string stage, long total) =>
        new(stage, total, () => DateTime.UtcNow, (s, m, p, d, t) => EmitProgress(opId, s, m, p, d, t).GetAwaiter().GetResult());

    private Task EmitOutput(string opId, string line)
        => _bus.EmitAsync(opId, "output", new { line });

    /// <summary>Uploads a decompressed backup stream; used by restore so the server never sees a compressed file.</summary>
    private async Task SftpUploadFromStreamAsync(ServerConfig server, Stream source, string remotePath, CancellationToken ct)
    {
        using var connection = await RoutedSftp.OpenAsync(_networks, server, _knownHosts, ct: ct);
        await Task.Run(() => connection.Client.UploadFile(source, remotePath), ct);
    }

    /// <summary>Pulls a provider-written file (SQL Server .bak) into the archive pipeline.</summary>
    private async Task SftpDownloadToStreamAsync(ServerConfig server, string remotePath, Stream destination, CancellationToken ct)
    {
        using var connection = await RoutedSftp.OpenAsync(_networks, server, _knownHosts, ct: ct);
        await Task.Run(() => connection.Client.DownloadFile(remotePath, destination), ct);
    }

    private async Task SftpUploadWithProgressAsync(ServerConfig server, string localPath, string remotePath, ProgressReporter progress, CancellationToken ct)
    {
        using var connection = await RoutedSftp.OpenAsync(_networks, server, _knownHosts, ct: ct);
        using var fs = File.OpenRead(localPath);
        connection.Client.UploadFile(fs, remotePath, uploaded => progress.Report((long)uploaded));
        progress.Report(fs.Length);
    }
}
