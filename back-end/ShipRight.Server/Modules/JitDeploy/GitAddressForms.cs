using System.Text.RegularExpressions;

namespace ShipRight.Modules.JitDeploy;

/// <summary>
/// One repository's address in the forms a person might need. <see cref="Original"/> is what was given, with any
/// password or token removed. <see cref="Ssh"/> and <see cref="Https"/> are only filled in for the usual
/// <c>host/owner/repo</c> shape; an unusual address (another port, a local path, Azure DevOps) keeps just its original form
/// rather than a guessed twin. <see cref="Key"/> is the same for the same repository in any form.
/// </summary>
public sealed record GitAddressForm(string Original, string? Ssh, string? Https, bool HadCredentials, string Key);

public static class GitAddressForms
{
    private static readonly Regex Scp = new(@"^(?<user>[A-Za-z0-9._-]+)@(?<host>[A-Za-z0-9.-]+):(?<path>[^/\s:][^\s]*)$", RegexOptions.Compiled);
    private static readonly Regex Url = new(@"^(?<scheme>[A-Za-z][A-Za-z0-9+.-]*)://(?:(?<ui>[^/@\s]+)@)?(?<host>[^/:@\s]+)(?::(?<port>\d+))?/(?<path>\S+)$", RegexOptions.Compiled);
    private static readonly Regex TokenShapes = new(
        @"\bgh[pousr]_[A-Za-z0-9]{20,}|\bgithub_pat_[A-Za-z0-9_]{20,}|\bglpat-[A-Za-z0-9_-]{16,}|\bAKIA[0-9A-Z]{16}\b",
        RegexOptions.Compiled);
    private static readonly Regex UserInfo = new(@"(?<scheme>\b[A-Za-z][A-Za-z0-9+.-]*://)[^/\s'""@]+@", RegexOptions.Compiled);

    public static GitAddressForm Analyse(string? raw)
    {
        var s = (raw ?? string.Empty).Trim();
        if (s.Length == 0) return new("", null, null, false, "");

        var scp = Scp.Match(s);
        if (scp.Success)
        {
            var host = scp.Groups["host"].Value;
            var path = scp.Groups["path"].Value;
            return IsUnusualHost(host)
                ? Plain(s)
                : new(s, s, $"https://{host}/{path}", false, KeyOf(host, path));
        }

        var m = Url.Match(s);
        if (!m.Success) return Plain(s);

        var scheme = m.Groups["scheme"].Value.ToLowerInvariant();
        var hasPort = m.Groups["port"].Success;
        var hostName = m.Groups["host"].Value;
        var p = m.Groups["path"].Value;
        var ui = m.Groups["ui"].Success ? m.Groups["ui"].Value : null;

        if (scheme == "https")
        {
            var original = $"https://{hostName}{(hasPort ? ":" + m.Groups["port"].Value : "")}/{p}";
            var had = ui is not null;
            if (hasPort || IsUnusualHost(hostName)) return new(original, null, original, had, KeyOf(hostName, p));
            return new(original, $"git@{hostName}:{p}", original, had, KeyOf(hostName, p));
        }

        if (scheme == "ssh")
        {
            var user = ui?.Split(':')[0];
            var had = ui is not null && ui.Contains(':');
            if (hasPort || IsUnusualHost(hostName))
                return new($"ssh://{(user is null ? "" : user + "@")}{hostName}{(hasPort ? ":" + m.Groups["port"].Value : "")}/{p}", null, null, had, KeyOf(hostName, p));
            var u = string.IsNullOrEmpty(user) ? "git" : user;
            return new($"ssh://{u}@{hostName}/{p}", $"{u}@{hostName}:{p}", $"https://{hostName}/{p}", had, KeyOf(hostName, p));
        }

        return Plain(s);
    }

    /// <summary>
    /// Hides the password or token inside any <c>scheme://user:secret@host</c>, and any well-known access-token shape
    /// (GitHub, GitLab, AWS) wherever it appears in the text.
    /// </summary>
    public static string Redact(string? text) =>
        string.IsNullOrEmpty(text) ? string.Empty : TokenShapes.Replace(UserInfo.Replace(text, "${scheme}***@"), "***");

    /// <summary>Also hides each given secret wherever it appears, whatever shape it has.</summary>
    public static string Redact(string? text, IReadOnlyCollection<string> secrets)
    {
        var t = Redact(text);
        foreach (var s in secrets.Where(x => x.Length >= 8)) t = t.Replace(s, "***", StringComparison.Ordinal);
        return t;
    }

    private static GitAddressForm Plain(string s) => new(s, null, null, false, s.ToLowerInvariant());

    private static bool IsUnusualHost(string host) =>
        host.Contains("dev.azure.com", StringComparison.OrdinalIgnoreCase) || host.Contains("visualstudio.com", StringComparison.OrdinalIgnoreCase);

    private static string KeyOf(string host, string path)
    {
        var p = path.Trim('/');
        if (p.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) p = p[..^4];
        return $"{host}/{p}".ToLowerInvariant();
    }
}
