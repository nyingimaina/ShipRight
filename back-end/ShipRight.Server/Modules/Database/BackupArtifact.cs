using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ShipRight.Modules.Database;

/// <summary>What one backup file is, independent of where the bytes live. Stored in a sidecar next to the file.</summary>
public sealed record BackupArtifactMeta
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;
    public string FileName { get; init; } = string.Empty;
    public string DatabaseName { get; init; } = string.Empty;
    /// <summary>sql or bak — the format of the bytes before compression.</summary>
    public string Format { get; init; } = "sql";
    /// <summary>none or zstd.</summary>
    public string Compression { get; init; } = BackupCompression.None;
    public int CompressionLevel { get; init; }
    /// <summary>none or aes256gcm.</summary>
    public string Encryption { get; init; } = BackupEncryption.None;
    public string KeyId { get; init; } = string.Empty;
    public long OriginalBytes { get; init; }
    public long StoredBytes { get; init; }
    public string Sha256 { get; init; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    /// <summary>manual or schedule.</summary>
    public string Source { get; init; } = BackupSource.Manual;
    public string? ScheduleId { get; init; }
    public long DurationMs { get; init; }

    public double Ratio => StoredBytes > 0 ? (double)OriginalBytes / StoredBytes : 1;
}

public static class BackupCompression
{
    public const string None = "none";
    public const string Zstd = "zstd";

    public static bool IsValid(int level) => level is >= 0 and <= 19;

    public static string NameFor(int level) => level <= 0 ? None : Zstd;

    public static int Clamp(int level) => level < 0 ? 0 : level > 19 ? 19 : level;
}

public static class BackupEncryption
{
    public const string None = "none";
    public const string AesGcm = "aes256gcm";
}

public static class BackupSource
{
    public const string Manual = "manual";
    public const string Schedule = "schedule";
}

/// <summary>Reads and writes the sidecar metadata, tolerating old artifacts that have none.</summary>
public static class BackupMetadata
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly Regex Timestamp = new(@"_(\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2})_", RegexOptions.Compiled);

    public static async Task WriteAsync(string path, BackupArtifactMeta meta, CancellationToken ct = default)
    {
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(meta, Json), ct);
        File.Move(temp, path, overwrite: true);
    }

    public static BackupArtifactMeta? Read(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<BackupArtifactMeta>(File.ReadAllText(path), Json)
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Rebuilds what we can for files written before metadata existed (or copied without it).</summary>
    public static BackupArtifactMeta Recover(string filePath, string databaseName)
    {
        var info = new FileInfo(filePath);
        var created = CreatedAtFromName(info.Name) ?? new DateTimeOffset(info.CreationTimeUtc, TimeSpan.Zero);
        var name = info.Name;

        return new BackupArtifactMeta
        {
            FileName = name,
            DatabaseName = databaseName,
            Format = name.Contains(".bak", StringComparison.OrdinalIgnoreCase) ? "bak" : "sql",
            Compression = name.EndsWith(".zst", StringComparison.OrdinalIgnoreCase) ||
                          name.EndsWith(".zst.enc", StringComparison.OrdinalIgnoreCase)
                ? BackupCompression.Zstd
                : BackupCompression.None,
            Encryption = name.EndsWith(".enc", StringComparison.OrdinalIgnoreCase)
                ? BackupEncryption.AesGcm
                : BackupEncryption.None,
            OriginalBytes = info.Length,
            StoredBytes = info.Length,
            CreatedAtUtc = created,
        };
    }

    public static DateTimeOffset? CreatedAtFromName(string fileName)
    {
        var match = Timestamp.Match(fileName);
        return match.Success &&
               DateTime.TryParseExact(match.Groups[1].Value, "yyyy-MM-dd_HH-mm-ss",
                   CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                   out var parsed)
            ? new DateTimeOffset(parsed, TimeSpan.Zero)
            : null;
    }

    public static string NewFileName(string databaseName, DateTimeOffset now, string source, string format,
        int compressionLevel, bool encrypted)
    {
        var safeDb = string.Concat(databaseName.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-'));
        var ext = format.Equals("bak", StringComparison.OrdinalIgnoreCase) ? "bak" : "sql";
        var compression = compressionLevel > 0 ? ".zst" : string.Empty;
        var encryption = encrypted ? ".enc" : string.Empty;
        return $"{safeDb}_{now:yyyy-MM-dd_HH-mm-ss}_{source}.{ext}{compression}{encryption}";
    }

    /// <summary>The file name a user expects after download: no storage-only compression or encryption suffixes.</summary>
    public static string OriginalFileName(BackupArtifactMeta meta)
    {
        var name = meta.FileName;
        if (name.EndsWith(".enc", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        if (meta.Compression == BackupCompression.Zstd && name.EndsWith(".zst", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        return name;
    }
}
