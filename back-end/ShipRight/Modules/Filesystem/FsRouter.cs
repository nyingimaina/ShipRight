using System.Diagnostics;
using System.Text;
using Renci.SshNet;
using ShipRight.Shared.SshRunner;

namespace ShipRight.Modules.Filesystem;

public static class FsRouter
{
    private const int MaxPathLength = 4096;
    private const int SshConnectTimeoutSecs = 15;
public static void MapFsRoutes(this WebApplication app)
    {
        app.MapGet("/api/fs/shortcuts", async () =>
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            var commonFolders = new[]
            {
                new { label = "Home",      path = home },
                new { label = "Desktop",   path = Environment.GetFolderPath(Environment.SpecialFolder.Desktop) },
                new { label = "Documents", path = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) },
                new { label = "Downloads", path = Path.Combine(home, "Downloads") },
            }
            .Where(f => !string.IsNullOrEmpty(f.path) && Directory.Exists(f.path))
            .ToList();

            // No DriveType filter — filtering silently drops drives on some Windows configurations.
            var drives = DriveInfo.GetDrives()
                .Select(d => new { label = d.Name.TrimEnd('\\'), path = d.RootDirectory.FullName })
                .ToList();

            // wsl --list --quiet reads the registry directly; works even with no distro running.
            var wslDistros = new List<object>();
            try
            {
                var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = "wsl",
                    Arguments = "--list --quiet",
                    RedirectStandardOutput = true,
                    StandardOutputEncoding = Encoding.Unicode,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                if (proc != null)
                {
                    var output = await proc.StandardOutput.ReadToEndAsync();
                    await proc.WaitForExitAsync();
                    wslDistros.AddRange(
                        output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                              .Select(n => n.Trim())
                              .Where(n => !string.IsNullOrEmpty(n))
                              .Select(n => (object)new { label = n, path = $@"\\wsl.localhost\{n}" })
                    );
                }
            }
            catch { /* WSL not installed */ }

            Console.WriteLine($"[fs/shortcuts] drives={drives.Count} commonFolders={commonFolders.Count} wsl={wslDistros.Count}");
            return Results.Ok(new { commonFolders, drives, wsl = wslDistros });
        });

        app.MapGet("/api/fs/list", (string? path, bool showHidden = false) =>
        {
            var dir = string.IsNullOrWhiteSpace(path)
                ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                : path;

            if (dir.Contains("..") || dir.Contains('\0') || dir.Length > MaxPathLength)
                return Results.BadRequest(new { isError = true, message = "Invalid path." });

            if (!Directory.Exists(dir))
                return Results.NotFound(new { isError = true, message = $"Directory not found: {dir}" });

            var parentDir = Path.GetDirectoryName(dir);

            try
            {
                var entries = Directory.GetFileSystemEntries(dir)
                    .Select(e =>
                    {
                        var name = Path.GetFileName(e);
                        var isDir = Directory.Exists(e);
                        return new { name, path = e, isDirectory = isDir };
                    })
                    .Where(e => showHidden || !e.name.StartsWith('.'))
                    .OrderByDescending(e => e.isDirectory)
                    .ThenBy(e => e.name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return Results.Ok(new { path = dir, parent = parentDir, entries });
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Ok(new { path = dir, parent = parentDir, entries = Array.Empty<object>() });
            }
        });

        app.MapGet("/api/fs/remote/list", async (string host, string user, string keyPath, string? path, KnownHostsStore knownHosts, bool showHidden = false) =>
        {
            if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(keyPath))
                return Results.BadRequest(new { isError = true, message = "host, user, and keyPath are required." });

            var remotePath = string.IsNullOrWhiteSpace(path) ? $"/home/{user}" : path;
            if (remotePath.Contains('\0'))
                return Results.BadRequest(new { isError = true, message = "Invalid path." });

            try
            {
                using var keyFile = await SshKeyLoader.LoadAsync(keyPath);
                using var sftp = new SftpClient(host, user, keyFile);

                sftp.ConnectionInfo.Timeout = TimeSpan.FromSeconds(SshConnectTimeoutSecs);
                sftp.HostKeyReceived += (_, e) =>
                {
                    var fingerprint = BitConverter.ToString(e.FingerPrint).Replace("-", ":").ToLowerInvariant();
                    knownHosts.Add(host, fingerprint);
                    e.CanTrust = true;
                };

                sftp.Connect();
                Console.WriteLine($"[fs/remote/list] connected {user}@{host} path={remotePath}");

                try
                {
                    var entries = sftp.ListDirectory(remotePath)
                        .Where(e => e.Name != "." && e.Name != "..")
                        .Where(e => showHidden || !e.Name.StartsWith('.'))
                        .OrderByDescending(e => e.IsDirectory)
                        .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                        .Select(e => new { name = e.Name, path = e.FullName, isDirectory = e.IsDirectory })
                        .ToList();

                    var parent = ComputeRemoteParent(remotePath);

                    return Results.Ok(new { path = remotePath, parent, entries });
                }
                finally
                {
                    sftp.Disconnect();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[fs/remote/list] error: {ex.Message}");
                return Results.BadRequest(new { isError = true, message = $"SSH error: {ex.Message}" });
            }
        });
    }

    private static string? ComputeRemoteParent(string remotePath)
    {
        var trimmed = remotePath.TrimEnd('/');
        if (string.IsNullOrEmpty(trimmed) || trimmed == "/") return null;
        var lastSlash = trimmed.LastIndexOf('/');
        return lastSlash <= 0 ? "/" : trimmed[..lastSlash];
    }

}
