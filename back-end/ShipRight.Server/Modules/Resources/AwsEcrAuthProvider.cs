using ShipRight.Modules.Resources.Models;
using ShipRight.Modules.Resources.Stores;
using ShipRight.Shared.CommandExecution;
using ShipRight.Shared.ProcessRunner;

namespace ShipRight.Modules.Resources;

/// <summary>
/// Acquires docker login credentials for Amazon ECR by running
/// `aws ecr get-login-password --region {region}` and returning ("AWS", token).
/// Selected for resources with AuthType = AwsEcr or for ECR hostnames with no
/// bound resource. The token is minted per push pipeline and never cached.
/// </summary>
public sealed class AwsEcrAuthProvider : IRegistryAuthProvider
{
    private static readonly TimeSpan EcrLoginTimeout = TimeSpan.FromSeconds(60);
    private readonly ICommandExecutor? _executor;
    private readonly IAwsProfileResourceStore? _profileStore;

    public AwsEcrAuthProvider(
        IProcessRunner? runner, IAwsProfileResourceStore? profileStore = null, ICommandExecutor? executor = null)
    {
        _profileStore = profileStore;
        _executor = executor ?? (runner is null ? null : CommandExecutor.PassthroughLocal(runner));
    }

    public bool IsDockerHub => false;

    public bool Supports(string registryHost, DockerRegistryResource? resource) =>
        resource?.AuthType == RegistryAuthType.AwsEcr ||
        (resource is null && IsAwsEcrHost(registryHost));

    public async Task<(string username, string password)> GetLoginCredentialsAsync(
        string registryHost, DockerRegistryResource? resource,
        string fallbackUsername, string fallbackPassword)
    {
        if (_executor is null)
            throw new InvalidOperationException("No process runner is available for ECR token retrieval.");

        var region = !string.IsNullOrEmpty(resource?.AwsRegion)
            ? resource!.AwsRegion
            : ParseRegionFromHost(registryHost);

        if (string.IsNullOrEmpty(region))
            throw new InvalidOperationException(
                $"Unable to determine the AWS region for ECR registry '{registryHost}'. Set AwsRegion on the registry resource.");

        var envOverride = await ResolveProfileEnvironmentAsync(resource);

        var result = await _executor.RunAsync("aws",
            ["ecr", "get-login-password", "--region", region],
            null,
            timeout: EcrLoginTimeout,
            envOverride: envOverride);

        if (!result.Success)
        {
            if (resource is not null && !string.IsNullOrEmpty(resource.Username) && !string.IsNullOrEmpty(resource.Password))
                return (resource.Username, resource.Password);
            throw new InvalidOperationException($"Failed to retrieve the ECR login token for {registryHost} (exit {result.ExitCode}).");
        }

        var token = result.StdOut.Trim();
        if (string.IsNullOrEmpty(token))
            throw new InvalidOperationException($"ECR returned an empty login token for {registryHost}.");
        return ("AWS", token);
    }

    internal static bool IsAwsEcrHost(string registryHost) =>
        !string.IsNullOrEmpty(registryHost) &&
        registryHost.EndsWith(".amazonaws.com", StringComparison.OrdinalIgnoreCase);

    // "123456789012.dkr.ecr.us-east-1.amazonaws.com" → "us-east-1"
    internal static string ParseRegionFromHost(string registryHost)
    {
        var parts = registryHost.Split('.');
        if (parts.Length >= 4 && parts[1] == "dkr" && parts[2] == "ecr")
            return parts[3];
        return string.Empty;
    }

    private async Task<IReadOnlyDictionary<string, string>?> ResolveProfileEnvironmentAsync(
        DockerRegistryResource? resource)
    {
        if (resource?.AwsProfileResourceId is not Guid profileId || _profileStore is null)
            return null;

        var profile = await _profileStore.GetByIdAsync(profileId);
        if (profile is null)
            throw new InvalidOperationException(
                $"AWS profile resource '{profileId}' referenced by registry '{resource.Name}' no longer exists.");

        var env = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(profile.DefaultRegion))
            env["AWS_DEFAULT_REGION"] = profile.DefaultRegion;

        if (profile.UsesExplicitKeys)
        {
            env["AWS_ACCESS_KEY_ID"] = profile.AccessKeyId;
            env["AWS_SECRET_ACCESS_KEY"] = profile.SecretAccessKey;
            if (!string.IsNullOrEmpty(profile.SessionToken))
                env["AWS_SESSION_TOKEN"] = profile.SessionToken;
        }
        else if (!string.IsNullOrEmpty(profile.ProfileName))
        {
            env["AWS_PROFILE"] = profile.ProfileName;
        }

        return env;
    }
}
