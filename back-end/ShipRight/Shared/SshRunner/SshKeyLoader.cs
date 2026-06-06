using System.Diagnostics;
using Renci.SshNet;

namespace ShipRight.Shared.SshRunner;

public static class SshKeyLoader
{
    public static async Task<PrivateKeyFile> LoadAsync(string keyPath)
    {
        if (!keyPath.StartsWith(@"\\"))
        {
            await FixKeyPermissionsAsync(keyPath);
            return new PrivateKeyFile(keyPath);
        }

        var (distro, linuxPath) = ParseUncPath(keyPath);
        if (distro is null)
            throw new InvalidOperationException($"Cannot resolve Linux path from UNC: {keyPath}");

        var winTempPath = Path.Combine(Path.GetTempPath(), $"sr_key_{Guid.NewGuid():N}.pem");
        var wslTempPath = WindowsToWslPath(winTempPath);

        var psi = new ProcessStartInfo
        {
            FileName = "wsl",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-d"); psi.ArgumentList.Add(distro);
        psi.ArgumentList.Add("-u"); psi.ArgumentList.Add("root");
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add("cp"); psi.ArgumentList.Add(linuxPath!); psi.ArgumentList.Add(wslTempPath);

        var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start wsl process for key copy.");
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException(
                $"wsl cp failed (exit {proc.ExitCode}): {stderr.Trim()}. Distro={distro} Path={linuxPath}");

        try
        {
            var currentUser = $"{Environment.UserDomainName}\\{Environment.UserName}";
            await RunSilentAsync("icacls", $"\"{winTempPath}\" /inheritance:r /grant:r \"{currentUser}:(R)\"");
            return new PrivateKeyFile(winTempPath);
        }
        finally
        {
            try { File.Delete(winTempPath); } catch { /* best-effort */ }
        }
    }

    private static (string? Distro, string? LinuxPath) ParseUncPath(string keyPath)
    {
        var parts = keyPath.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) return (null, null);
        return (parts[1].Trim(), "/" + string.Join("/", parts[2..]));
    }

    private static string WindowsToWslPath(string winPath)
    {
        var fwd = winPath.Replace('\\', '/');
        if (fwd.Length >= 2 && fwd[1] == ':')
            return $"/mnt/{char.ToLower(fwd[0])}{fwd[2..]}";
        return fwd;
    }

    private static async Task FixKeyPermissionsAsync(string keyPath)
    {
        if (keyPath.StartsWith(@"\\")) return;
        try
        {
            var currentUser = $"{Environment.UserDomainName}\\{Environment.UserName}";
            await RunSilentAsync("icacls", $"\"{keyPath}\" /inheritance:r /grant:r \"{currentUser}:(R)\"");
        }
        catch { /* best-effort */ }
    }

    private static async Task RunSilentAsync(string fileName, string args)
    {
        var proc = Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        if (proc != null) await proc.WaitForExitAsync();
    }
}
