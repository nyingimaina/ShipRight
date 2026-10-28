using System.Text.RegularExpressions;
using ShipRight.Modules.Projects;
using ShipRight.Modules.Resources.Models;

namespace ShipRight.Modules.Transfer;

/// <summary>Blanks every secret field. Whatever this misses would be written into the zip, so it is the one place to extend.</summary>
internal static class BundleSecrets
{
    public static ProjectConfig Strip(ProjectConfig p) => p with
    {
        Services = p.Services.Select(s => s with { DockerPassword = string.Empty }).ToList(),
        Database = p.Database is null ? null : p.Database with { RootPassword = string.Empty },
        Server = Strip(p.Server),
        Wsl = p.Wsl with { ComposeRepoUrl = StripUrlCredentials(p.Wsl.ComposeRepoUrl) },
    };

    public static ServerConfig Strip(ServerConfig s) =>
        s with { EnvFile = string.Empty, ComposeRepoUrl = StripUrlCredentials(s.ComposeRepoUrl) };

    public static DockerRegistryResource Strip(DockerRegistryResource r) => r with { Password = string.Empty };

    public static AwsProfileResource Strip(AwsProfileResource a) =>
        a with { AccessKeyId = string.Empty, SecretAccessKey = string.Empty, SessionToken = string.Empty };

    public static CredentialResource Strip(CredentialResource c) => c with { Value = string.Empty };

    private static readonly Regex UrlUserInfo = new(@"^(https?://)[^/@\s]+@", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>True when an http(s) address carries a token or user:password before the host.</summary>
    public static bool HasUrlCredentials(string? url) => !string.IsNullOrEmpty(url) && UrlUserInfo.IsMatch(url);

    /// <summary>Drops "token@" / "user:password@" from an http(s) address; ssh and scp-style addresses are left alone.</summary>
    public static string StripUrlCredentials(string url) => UrlUserInfo.Replace(url, "$1");
}
