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
    };

    public static ServerConfig Strip(ServerConfig s) => s with { EnvFile = string.Empty };

    public static DockerRegistryResource Strip(DockerRegistryResource r) => r with { Password = string.Empty };

    public static AwsProfileResource Strip(AwsProfileResource a) =>
        a with { AccessKeyId = string.Empty, SecretAccessKey = string.Empty, SessionToken = string.Empty };

    public static CredentialResource Strip(CredentialResource c) => c with { Value = string.Empty };
}
