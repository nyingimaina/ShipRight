using System.Text.RegularExpressions;

namespace ShipRight.Modules.RemoteHost;

/// <summary>
/// Prepare-time check that the HTTPS domain (SHIPRIGHT_DOMAIN in the server's env) points at this server.
/// It only warns: DNS may legitimately still be propagating, and a warning must never stop a Prepare.
/// </summary>
public static class RemoteDomainCheck
{
    private static readonly Regex Assignment = new(
        @"^\s*(?:export\s+)?SHIPRIGHT_DOMAIN\s*=\s*[""']?([^""'#\s]*)", RegexOptions.Multiline | RegexOptions.Compiled);

    // Plain public host name: labels of letters/digits/hyphens, at least one dot, no leading hyphen.
    private static readonly Regex HostName = new(
        @"^(?!-)[a-z0-9-]{1,63}(?<!-)(\.(?!-)[a-z0-9-]{1,63}(?<!-))+$", RegexOptions.Compiled);

    /// <summary>The domain set in the env text (last assignment wins, like a shell), or null when unset or not a safe host name.</summary>
    public static string? ExtractDomain(string? envFile)
    {
        if (string.IsNullOrWhiteSpace(envFile)) return null;
        var matches = Assignment.Matches(envFile.Replace("\r", ""));
        if (matches.Count == 0) return null;
        var value = matches[^1].Groups[1].Value.Trim().ToLowerInvariant();
        return HostName.IsMatch(value) ? value : null;
    }

    /// <summary>Always exits 0: prints the server's public IP, what the domain resolves to, and what to do about a mismatch.</summary>
    public static string BuildCommand(string domain) =>
        $"d='{domain}'; " +
        "ip=$(curl -s --max-time 5 https://checkip.amazonaws.com | tr -d '[:space:]'); " +
        "dns=$(getent ahostsv4 \"$d\" 2>/dev/null | awk '{print $1}' | sort -u | tr '\\n' ' '); " +
        "echo \"[domain] This server's public IP: ${ip:-unknown}\"; " +
        "echo \"[domain] $d resolves to: ${dns:-nothing}\"; " +
        "if [ -z \"$dns\" ]; then " +
        "echo \"[domain] WARNING: $d has no DNS record yet. Add an A record for it pointing at ${ip:-this server}; HTTPS cannot start until it exists.\"; " +
        "elif [ -n \"$ip\" ] && ! echo \" $dns\" | grep -q \" $ip \"; then " +
        "echo \"[domain] WARNING: $d points to ${dns}and not to this server ($ip). Fix the A record (ignore this if a proxy such as Cloudflare is in front).\"; " +
        "else echo \"[domain] OK: $d points at this server. Make sure ports 80 and 443 are open in the firewall / security group.\"; fi; true";

    public static string BuildMissingDomainCommand() =>
        "echo '[domain] WARNING: SHIPRIGHT_DOMAIN is not set in the environment settings for this server. " +
        "Add a line like SHIPRIGHT_DOMAIN=shipright.example.com, or Deploy will stop and HTTPS will not start.'; true";
}
