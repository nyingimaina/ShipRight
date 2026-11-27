using System.Text.RegularExpressions;

namespace ShipRight.Modules.Git;

public sealed record GitUrlInfo(string Provider, string Owner, string Name, string HttpsUrl, string SshUrl)
{
    public string FullName => $"{Owner}/{Name}";
}

/// <summary>
/// Parses the addresses people actually clone: https, scp-style and ssh://. It refuses an address that carries a
/// password or token, because ShipRight never keeps a secret inside an address.
/// </summary>
public static class GitUrl
{
    private static readonly Regex Https = new(
        @"^https?://(?<host>[^/:@\s]+)(?::\d+)?/(?<owner>[^/\s]+)/(?<repo>[^/\s]+?)(?:\.git)?/?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Scp = new(
        @"^(?<user>[A-Za-z0-9._-]+)@(?<host>[A-Za-z0-9.-]+):(?<owner>[^/\s]+)/(?<repo>[^/\s]+?)(?:\.git)?$",
        RegexOptions.Compiled);

    private static readonly Regex Ssh = new(
        @"^ssh://(?<user>[A-Za-z0-9._-]+)@(?<host>[A-Za-z0-9.-]+)(?::\d+)?/(?<owner>[^/\s]+)/(?<repo>[^/\s]+?)(?:\.git)?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Credentials = new(@"^https?://[^/@\s]+@", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static GitUrlInfo? Parse(string? raw)
    {
        var value = (raw ?? string.Empty).Trim();
        if (value.Length == 0) return null;
        if (Credentials.IsMatch(value)) return null;

        var match = Https.Match(value);
        if (match.Success) return Build(match);

        match = Scp.Match(value);
        if (match.Success) return Build(match);

        match = Ssh.Match(value);
        if (match.Success) return Build(match);

        return null;
    }

    private static GitUrlInfo Build(Match match)
    {
        var host = match.Groups["host"].Value;
        var owner = match.Groups["owner"].Value;
        var repo = match.Groups["repo"].Value.TrimEnd('/');
        if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) repo = repo[..^4];

        var provider = host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ? "github" : "generic";
        return new GitUrlInfo(
            provider, owner, repo,
            $"https://{host}/{owner}/{repo}",
            $"git@{host}:{owner}/{repo}.git");
    }
}
