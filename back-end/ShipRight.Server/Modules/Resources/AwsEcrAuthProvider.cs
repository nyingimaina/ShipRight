using ShipRight.Modules.Resources.Models;
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
    private readonly IProcessRunner? _runner;

    public AwsEcrAuthProvider(IProcessRunner? runner)
    {
        _runner = runner;
    }

    public bool IsDockerHub => false;

    public bool Supports(string registryHost, DockerRegistryResource? resource) =>
        resource?.AuthType == RegistryAuthType.AwsEcr ||
        (resource is null && IsAwsEcrHost(registryHost));

    public async Task<(string username, string password)> GetLoginCredentialsAsync(
        string registryHost, DockerRegistryResource? resource,
        string fallbackUsername, string fallbackPassword)
    {
        if (_runner is null)
            throw new InvalidOperationException("No process runner is available for ECR token retrieval.");

        var region = !string.IsNullOrEmpty(resource?.AwsRegion)
            ? resource!.AwsRegion
            : ParseRegionFromHost(registryHost);

        if (string.IsNullOrEmpty(region))
            throw new InvalidOperationException(
                $"Unable to determine the AWS region for ECR registry '{registryHost}'. Set AwsRegion on the registry resource.");

        var result = await _runner.RunAsync("aws",
            ["ecr", "get-login-password", "--region", region],
            null,
            timeout: EcrLoginTimeout);

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
}
