using System.Security.Cryptography;
using Serilog;
using ZstdSharp;

namespace ShipRight.Modules.Database;

public sealed record BackupWriteOptions(
    string ProjectId,
    string DatabaseName,
    string Format,
    string Source,
    int CompressionLevel,
    bool Encrypt,
    string? ScheduleId = null);

public sealed record BackupStorageInfo(
    string Root,
    bool InsideDataDirectory,
    long AvailableBytes,
    long UsedBytes,
    int Count,
    DateTimeOffset? Oldest,
    DateTimeOffset? Newest);

public enum BackupReadMode
{
    /// <summary>Decrypted and decompressed: the bytes a user imports with mysql/psql.</summary>
    Plain,
    /// <summary>Decrypted, still zstd-compressed: smaller transfer for someone with zstd installed.</summary>
    Compressed,
    /// <summary>The file exactly as stored (may be encrypted).</summary>
    Stored,
}

/// <summary>
/// Reads and writes backup artifacts: hash while copying, zstd-compress, then seal with the active backup key.
/// The metadata sidecar is the source of truth for a file; old files without one are recovered from the file
/// name and size so nothing disappears from the list after an upgrade.
/// </summary>
public static class BackupArchive
{
    private const int CopyBuffer = 128 * 1024;

    public static async Task<BackupArtifactMeta> WriteAsync(
        BackupStorage storage, BackupKeyring keyring, BackupWriteOptions options, Stream source,
        Func<long, Task>? progress, CancellationToken ct)
    {
        var started = global::System.Diagnostics.Stopwatch.GetTimestamp();
        var now = DateTimeOffset.UtcNow;
        var level = BackupCompression.Clamp(options.CompressionLevel);
        var directory = storage.ProjectDirectory(options.ProjectId);

        var attempt = 0;
        string fileName;
        string finalPath;
        do
        {
            attempt++;
            var runSource = attempt == 1 ? options.Source : $"{options.Source}-{attempt}";
            fileName = BackupMetadata.NewFileName(options.DatabaseName, now, runSource,
                options.Format, level, options.Encrypt);
            finalPath = Path.Combine(directory, fileName);
        } while (File.Exists(finalPath));

        var tempPath = finalPath + ".partial";
        var metaPath = finalPath + ".meta.json";

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        BackupKey? key = null;
        if (options.Encrypt) key = await keyring.GetActiveAsync(ct);

        await using (var file = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None,
                         CopyBuffer, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            var encryption = key is null ? (Stream?)null : new BackupEncryptStream(file, key.Material, key.Id);
            Stream sink = encryption ?? file;
            var compressor = level > 0 ? new CompressionStream(sink, level, leaveOpen: true) : null;
            var head = (Stream?)compressor ?? sink;

            long original = 0;
            var buffer = new byte[CopyBuffer];
            var reported = 0L;
            try
            {
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    await head.WriteAsync(buffer.AsMemory(0, read), ct);
                    hash.AppendData(buffer, 0, read);
                    original += read;
                    if (progress is not null && original - reported >= 4 * 1024 * 1024)
                    {
                        reported = original;
                        await progress(original);
                    }
                }

                if (progress is not null) await progress(original);
                if (compressor is not null) await compressor.DisposeAsync();
                if (encryption is not null) await encryption.DisposeAsync();
                await file.FlushAsync(ct);

                var meta = new BackupArtifactMeta
                {
                    FileName = fileName,
                    DatabaseName = options.DatabaseName,
                    Format = options.Format,
                    Compression = BackupCompression.NameFor(level),
                    CompressionLevel = level,
                    Encryption = key is null ? BackupEncryption.None : BackupEncryption.AesGcm,
                    KeyId = key?.Id ?? string.Empty,
                    OriginalBytes = original,
                    StoredBytes = file.Length,
                    Sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
                    CreatedAtUtc = now,
                    Source = options.Source,
                    ScheduleId = options.ScheduleId,
                    DurationMs = (long)global::System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                };

                file.Close();
                File.Move(tempPath, finalPath, overwrite: false);
                await BackupMetadata.WriteAsync(metaPath, meta, ct);
                Log.Information("Backup written: {File} ({Stored}/{Original} bytes, {Compression})",
                    fileName, meta.StoredBytes, meta.OriginalBytes, meta.Compression);
                return meta;
            }
            catch
            {
                TryDelete(tempPath);
                throw;
            }
        }
    }

    public static async Task<(Stream Stream, BackupArtifactMeta Meta)> OpenReadAsync(
        BackupStorage storage, string projectId, string fileName, BackupKeyring keyring,
        BackupReadMode mode = BackupReadMode.Plain, CancellationToken ct = default)
    {
        var path = storage.ResolveArtifact(projectId, fileName)
                   ?? throw new FileNotFoundException($"Backup '{fileName}' was not found.", fileName);
        var meta = BackupMetadata.Read(storage.MetadataPath(projectId, fileName))
                   ?? BackupMetadata.Recover(path, DatabaseNameFrom(fileName));

        var file = File.Open(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
        });

        Stream head = file;
        try
        {
            if (mode != BackupReadMode.Stored && meta.Encryption == BackupEncryption.AesGcm)
            {
                var key = await keyring.FindKeyAsync(meta.KeyId, ct)
                          ?? throw new InvalidOperationException(
                              $"The backup key '{meta.KeyId}' is missing, so this file cannot be opened. Import the matching recovery file.");
                head = new BackupDecryptStream(head, _ => key);
            }

            if (mode == BackupReadMode.Plain && meta.Compression == BackupCompression.Zstd)
                head = new DecompressionStream(head, leaveOpen: true);

            return (ReferenceEquals(head, file) ? new StreamChain(file) : new StreamChain(file, head), meta);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    public static IReadOnlyList<BackupArtifactMeta> List(BackupStorage storage, string projectId)
    {
        var dir = Path.Combine(storage.Root, projectId);
        if (!Directory.Exists(dir)) return [];

        var artifacts = new List<BackupArtifactMeta>();
        foreach (var path in Directory.EnumerateFiles(dir))
        {
            if (path.EndsWith(".meta.json", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".partial", StringComparison.OrdinalIgnoreCase))
                continue;

            var name = Path.GetFileName(path);
            var meta = BackupMetadata.Read(path + ".meta.json")
                       ?? BackupMetadata.Recover(path, DatabaseNameFrom(name));
            artifacts.Add(meta with
            {
                FileName = name,
                StoredBytes = new FileInfo(path).Length,
            });
        }

        return artifacts
            .OrderByDescending(a => a.CreatedAtUtc)
            .ThenByDescending(a => a.FileName, StringComparer.Ordinal)
            .ToList();
    }

    public static bool Delete(BackupStorage storage, string projectId, string fileName)
    {
        var path = storage.ResolveArtifact(projectId, fileName);
        if (path is null) return false;

        File.Delete(path);
        TryDelete(storage.MetadataPath(projectId, fileName));
        Log.Information("Backup deleted: {File}", fileName);
        return true;
    }

    public static BackupStorageInfo Info(BackupStorage storage)
    {
        var all = Directory.Exists(storage.Root)
            ? Directory.EnumerateDirectories(storage.Root)
                .SelectMany(d => List(storage, Path.GetFileName(d)))
                .ToList()
            : [];

        return new BackupStorageInfo(
            storage.Root,
            storage.IsInsideDataDirectory(),
            storage.AvailableBytes(),
            all.Sum(a => a.StoredBytes),
            all.Count,
            all.Count == 0 ? null : all.Min(a => a.CreatedAtUtc),
            all.Count == 0 ? null : all.Max(a => a.CreatedAtUtc));
    }

    /// <summary>Every key that artifacts still reference, so a retire can refuse to break a restore.</summary>
    public static HashSet<string> ReferencedKeyIds(BackupStorage storage)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (!Directory.Exists(storage.Root)) return ids;
        foreach (var dir in Directory.EnumerateDirectories(storage.Root))
        foreach (var meta in List(storage, Path.GetFileName(dir)))
        {
            if (!string.IsNullOrEmpty(meta.KeyId)) ids.Add(meta.KeyId);
        }
        return ids;
    }

    public static string DatabaseNameFrom(string fileName)
    {
        var match = global::System.Text.RegularExpressions.Regex.Match(fileName,
            @"^(?<db>.+?)_\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}_");
        return match.Success ? match.Groups["db"].Value : Path.GetFileNameWithoutExtension(fileName);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { Log.Warning(ex, "Could not remove temporary backup file {Path}", path); }
    }
}
