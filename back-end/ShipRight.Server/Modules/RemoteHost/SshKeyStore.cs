using System.Diagnostics;
using Serilog;

namespace ShipRight.Modules.RemoteHost;

public class SshKeyStore
{
    private readonly string _dataDir;

    public SshKeyStore(string dataDir)
    {
        _dataDir = dataDir;
    }

    private string SshDir => Path.Combine(_dataDir, "ssh");

    public string GetPrivateKeyPath(string projectId) =>
        Path.Combine(SshDir, $"{projectId}.pem");

    private string PublicKeyPath(string projectId) =>
        Path.Combine(SshDir, $"{projectId}.pub");

    public Task<bool> ExistsAsync(string projectId) =>
        Task.FromResult(File.Exists(GetPrivateKeyPath(projectId)));

    public async Task<string> GetPublicKeyAsync(string projectId)
    {
        var path = PublicKeyPath(projectId);
        if (!File.Exists(path))
            throw new FileNotFoundException($"No managed SSH key for project '{projectId}'.");
        return (await File.ReadAllTextAsync(path)).Trim();
    }

    public async Task GenerateAsync(string projectId)
    {
        Directory.CreateDirectory(SshDir);

        var keyPath = GetPrivateKeyPath(projectId);
        var pubPath = PublicKeyPath(projectId);

        // Remove existing pair so ssh-keygen doesn't prompt to overwrite
        if (File.Exists(keyPath)) File.Delete(keyPath);
        if (File.Exists(pubPath)) File.Delete(pubPath);

        // ssh-keygen writes private key at <keyPath> and public key at <keyPath>.pub
        var psi = new ProcessStartInfo
        {
            FileName = "ssh-keygen",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-t"); psi.ArgumentList.Add("ed25519");
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add(keyPath);
        psi.ArgumentList.Add("-N"); psi.ArgumentList.Add("");
        psi.ArgumentList.Add("-C"); psi.ArgumentList.Add($"shipright-{projectId}");

        var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start ssh-keygen.");

        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();

        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"ssh-keygen failed (exit {proc.ExitCode}): {stderr.Trim()}");

        // ssh-keygen names the public key <keyPath>.pub — rename to our canonical path
        var generatedPubPath = keyPath + ".pub";
        if (File.Exists(generatedPubPath) && generatedPubPath != pubPath)
            File.Move(generatedPubPath, pubPath);

        RestrictKeyPermissions(keyPath);
        Log.Information("Generated managed SSH key for project {ProjectId}", projectId);
    }

    private static void RestrictKeyPermissions(string keyPath)
    {
        if (OperatingSystem.IsWindows()) return;
        // chmod 0600 on Linux/macOS
        var proc = Process.Start(new ProcessStartInfo("chmod", $"0600 \"{keyPath}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        });
        proc?.WaitForExit(3000);
    }
}
