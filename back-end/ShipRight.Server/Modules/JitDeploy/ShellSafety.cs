using System.Text.RegularExpressions;

namespace ShipRight.Modules.JitDeploy;

/// <summary>POSIX single-quoting: the only way a value may be placed inside a remote shell command.</summary>
public static class ShellQuote
{
    public static string Single(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Contains('\0')) throw new ArgumentException("A value sent to the server cannot contain a NUL character.", nameof(value));
        return "'" + value.Replace("'", "'\\''") + "'";
    }
}

/// <summary>
/// Allow-lists for every name that reaches a remote command. Anything that is not obviously safe is refused before
/// it is quoted, so a hostile branch name, service name or URL cannot become an option or a command. Each returns
/// the value (lower-cased where case does not matter) or throws <see cref="ArgumentException"/>.
/// </summary>
public static class JitNames
{
    private static readonly Regex ServiceRx = new(@"^[A-Za-z0-9][A-Za-z0-9_.-]*$", RegexOptions.Compiled);
    private static readonly Regex BranchRx = new(@"^[A-Za-z0-9][A-Za-z0-9/_.-]*$", RegexOptions.Compiled);
    private static readonly Regex ShaRx = new(@"^[0-9a-fA-F]{7,64}$", RegexOptions.Compiled);
    private static readonly Regex ImageRx = new(@"^[a-z0-9][a-z0-9._/:@-]*$", RegexOptions.Compiled);
    private static readonly Regex TagVarRx = new(@"^[A-Z][A-Z0-9_]*$", RegexOptions.Compiled);
    private static readonly Regex ProjectRx = new(@"^[a-z0-9][a-z0-9_-]*$", RegexOptions.Compiled);

    public static string Service(string v) =>
        Check(ServiceRx.IsMatch(v ?? string.Empty), v, "service name", "letters, digits, '.', '_' and '-', starting with a letter or digit");

    public static string Branch(string v)
    {
        var ok = BranchRx.IsMatch(v ?? string.Empty)
                 && !v!.Contains("..") && !v.Contains("//") && !v.EndsWith('/') && !v.EndsWith(".lock", StringComparison.Ordinal);
        return Check(ok, v, "branch name", "letters, digits, '/', '.', '_' and '-'");
    }

    public static string Sha(string v) =>
        Check(ShaRx.IsMatch(v ?? string.Empty), v, "commit id", "7 to 64 hexadecimal characters").ToLowerInvariant();

    public static string ImageRef(string v) =>
        Check(ImageRx.IsMatch(v ?? string.Empty), v, "image name", "lower-case letters, digits and '.', '_', '/', ':', '@', '-'");

    public static string TagVariable(string v) =>
        Check(TagVarRx.IsMatch(v ?? string.Empty), v, "tag variable name", "UPPER_CASE letters, digits and '_'");

    private static readonly global::System.Text.RegularExpressions.Regex RepoFileRx =
        new(@"^[A-Za-z0-9._][A-Za-z0-9._/-]*\.(yml|yaml)$", global::System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>A compose file inside the repository: a relative path ending in .yml or .yaml, with no '..', no empty parts and nothing a shell would treat specially.</summary>
    public static string RepoFile(string v) =>
        Check(RepoFileRx.IsMatch(v ?? string.Empty) && !v!.Split('/').Any(p => p.Length == 0 || p == ".."), v, "compose file",
            "a path inside your repository ending in .yml or .yaml, such as deploy/docker-compose.yml");

    public static string Project(string v) =>
        Check(ProjectRx.IsMatch(v ?? string.Empty), v, "compose project name", "lower-case letters, digits, '_' and '-'");

    public static string RepoUrl(string v)
    {
        if (!string.IsNullOrEmpty(v) && GitAddressForms.Analyse(v).HadCredentials)
            throw new ArgumentException("The git address contains a password or token. ShipRight does not keep secrets inside an address. Use the SSH form (git@host:owner/repo.git) with a deploy key.");
        var ok = !string.IsNullOrEmpty(v) && !v.StartsWith('-') && !v.Contains("::")
                 && !v.Any(c => char.IsControl(c) || char.IsWhiteSpace(c));
        return Check(ok, v, "git address", "an ordinary git URL without spaces or control characters, not starting with '-'");
    }

    public static string Path(string v)
    {
        var ok = !string.IsNullOrEmpty(v) && v.StartsWith('/') && v.TrimEnd('/').Length > 0
                 && !v.Any(char.IsControl) && !v.Split('/').Contains("..");
        return Check(ok, v, "folder", "an absolute path (starting with '/') without '..' or control characters, and not the root");
    }

    private static string Check(bool ok, string? value, string what, string rule)
    {
        if (!ok) throw new ArgumentException($"'{Printable(value)}' is not a valid {what}: use {rule}.");
        return value!;
    }

    private static string Printable(string? v) =>
        v is null ? "(nothing)" : new string(v.Select(c => char.IsControl(c) ? '?' : c).ToArray());
}

/// <summary>Where a project lives on its server. The repo and the state sit side by side so cleaning the repo never touches state.</summary>
public sealed record JitTarget
{
    public JitTarget(string host, string username, string sshKeyPath, string projectDir)
    {
        Host = host;
        Username = username;
        SshKeyPath = sshKeyPath;
        ProjectDir = (projectDir ?? string.Empty).TrimEnd('/');
    }

    public string Host { get; }
    public string Username { get; }
    public string SshKeyPath { get; }
    public string ProjectDir { get; }
    public string RepoDir => ProjectDir + "/repo";
    public string StateDir => ProjectDir + "/.deploy-state";
    public string StateFile => StateDir + "/state.json";
}
