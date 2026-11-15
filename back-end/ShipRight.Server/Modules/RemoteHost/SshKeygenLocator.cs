namespace ShipRight.Modules.RemoteHost;

/// <summary>
/// Finds ssh-keygen without relying on the app's PATH alone. An app started from the installer or a service often has a
/// shorter PATH than a terminal, so Windows' optional OpenSSH Client and Git for Windows are checked explicitly.
/// </summary>
public static class SshKeygenLocator
{
    public static string? Find(Func<string, bool> exists, IEnumerable<string> pathDirs, string windowsDir, string programFilesDir, bool windows)
    {
        var name = windows ? "ssh-keygen.exe" : "ssh-keygen";
        var candidates = pathDirs.Where(d => !string.IsNullOrWhiteSpace(d)).Select(d => Path.Combine(d.Trim().Trim('"'), name)).ToList();
        if (windows)
        {
            candidates.Add(Path.Combine(windowsDir, "System32", "OpenSSH", name));
            candidates.Add(Path.Combine(programFilesDir, "Git", "usr", "bin", name));
            candidates.Add(Path.Combine(programFilesDir, "Git", "bin", name));
        }
        return candidates.FirstOrDefault(exists);
    }

    public static string? FindOnThisMachine() => Find(
        File.Exists,
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator),
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        OperatingSystem.IsWindows());
}
