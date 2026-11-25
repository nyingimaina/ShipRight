using Microsoft.AspNetCore.WebUtilities;
using Serilog;

namespace ShipRight.Modules.Database;

/// <summary>
/// "Decrypt a downloaded backup": upload the artifact and recovery JSON, then stream the plain dump back
/// in a second request so large files go straight to disk instead of through browser memory.
/// </summary>
public static class BackupToolRouter
{
    private const long MaxArtifactBytes = 4L * 1024 * 1024 * 1024;
    private const long MaxRecoveryBytes = 1024 * 1024;
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    private static string Root => Path.Combine(Path.GetTempPath(), "shipright-decrypt");

    public static void MapBackupToolRoutes(this WebApplication app)
    {
        app.MapPost("/api/backups/decrypt", async (HttpRequest request, CancellationToken ct) =>
        {
            if (!request.HasFormContentType)
                return BadRequest("Choose the backup file and (for an encrypted backup) the recovery JSON file.");

            var boundary = BoundaryFrom(request.ContentType);
            if (string.IsNullOrEmpty(boundary))
                return BadRequest("The upload was not a valid form.");

            CleanupExpired();
            var token = Guid.NewGuid().ToString("N");
            var directory = Path.Combine(Root, token);
            Directory.CreateDirectory(directory);
            var keep = false;

            try
            {
                var reader = new MultipartReader(boundary, request.Body);
                string? artifactPath = null;
                string? recoveryPath = null;

                MultipartSection? section;
                while ((section = await reader.ReadNextSectionAsync(ct)) is not null)
                {
                    var name = section.GetContentDispositionHeader()?.Name.Value?.Trim('"');
                    if (name is not ("artifact" or "recovery")) continue;

                    var limit = name == "artifact" ? MaxArtifactBytes : MaxRecoveryBytes;
                    var path = Path.Combine(directory, name);
                    long total = 0;

                    await using (var output = File.Create(path))
                    {
                        var buffer = new byte[128 * 1024];
                        int read;
                        while ((read = await section.Body.ReadAsync(buffer, ct)) > 0)
                        {
                            total += read;
                            if (total > limit)
                                return TooLarge(name == "artifact"
                                    ? "The backup file is larger than the 4 GB limit for the decrypt tool."
                                    : "The recovery file is too large to be a ShipRight recovery file.");
                            await output.WriteAsync(buffer.AsMemory(0, read), ct);
                        }
                    }

                    if (name == "artifact") artifactPath = path;
                    else recoveryPath = path;
                }

                if (artifactPath is null || new FileInfo(artifactPath).Length == 0)
                    return BadRequest("Choose the backup file to decrypt.");

                var recoveryJson = recoveryPath is null ? null : await File.ReadAllTextAsync(recoveryPath, ct);
                string fileName;
                try
                {
                    var (probe, name) = await BackupDecryptor.OpenAsync(artifactPath, recoveryJson, ct);
                    await probe.DisposeAsync();
                    fileName = name;
                }
                catch (InvalidOperationException ex)
                {
                    return BadRequest(ex.Message);
                }
                catch (InvalidDataException ex)
                {
                    return BadRequest($"That file could not be read: {ex.Message}");
                }

                Log.Information("Backup decrypt prepared ({Name})", fileName);
                keep = true;
                return Results.Ok(new { token, fileName });
            }
            catch (OperationCanceledException)
            {
                return Results.StatusCode(499);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Backup decrypt upload failed");
                return Results.Json(new { isError = true, message = $"Could not read that file: {ex.Message}" },
                    statusCode: 500);
            }
            finally
            {
                // The download request owns the folder from here on. Failed preparations leave nothing behind.
                if (!keep) Cleanup(directory);
            }
        });

        app.MapGet("/api/backups/decrypt/{token}", async (string token, HttpContext http, CancellationToken ct) =>
        {
            if (!IsToken(token))
                return Results.NotFound(new { isError = true, message = "That decryption link is not valid." });

            var directory = Path.Combine(Root, token);
            var artifact = Path.Combine(directory, "artifact");
            if (!File.Exists(artifact))
                return Results.NotFound(new { isError = true, message = "That decryption link has expired. Upload the file again." });

            var recoveryPath = Path.Combine(directory, "recovery");
            var recoveryJson = File.Exists(recoveryPath) ? await File.ReadAllTextAsync(recoveryPath, ct) : null;

            Stream stream;
            string fileName;
            try
            {
                (stream, fileName) = await BackupDecryptor.OpenAsync(artifact, recoveryJson, ct);
            }
            catch (InvalidOperationException ex)
            {
                Cleanup(directory);
                return BadRequest(ex.Message);
            }
            catch (InvalidDataException ex)
            {
                Cleanup(directory);
                return BadRequest($"That file could not be read: {ex.Message}");
            }

            http.Response.OnCompleted(() =>
            {
                try { stream.Dispose(); } catch { /* best-effort */ }
                Cleanup(directory);
                return Task.CompletedTask;
            });

            return Results.Stream(async output => await stream.CopyToAsync(output, 128 * 1024, ct),
                "application/octet-stream", fileName);
        });
    }

    private static IResult BadRequest(string message) =>
        Results.Json(new { isError = true, message }, statusCode: 400);

    private static string? BoundaryFrom(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType)) return null;
        foreach (var part in contentType.Split(';', StringSplitOptions.TrimEntries))
        {
            if (part.StartsWith("boundary=", StringComparison.OrdinalIgnoreCase))
                return part["boundary=".Length..].Trim('"');
        }
        return null;
    }

    private static IResult TooLarge(string message) =>
        Results.Json(new { isError = true, message }, statusCode: 413);

    private static bool IsToken(string token) =>
        token.Length == 32 && token.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void CleanupExpired()
    {
        try
        {
            if (!Directory.Exists(Root)) return;
            foreach (var directory in Directory.EnumerateDirectories(Root))
            {
                if (DateTime.UtcNow - Directory.GetCreationTimeUtc(directory) > Lifetime)
                    Cleanup(directory);
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not clean up expired decrypt folders");
        }
    }

    private static void Cleanup(string directory)
    {
        try { Directory.Delete(directory, recursive: true); }
        catch (Exception ex) { Log.Debug(ex, "Could not remove decrypt folder {Dir}", directory); }
    }
}
