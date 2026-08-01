using ShipRight.Modules.Resources.Models;

namespace ShipRight.Modules.Resources;

/// <summary>
/// Supplies docker login credentials for a registry host.
/// Providers are selected by RegistryAuthProviderRegistry based on the
/// resolved host and the optional bound DockerRegistryResource.
/// </summary>
public interface IRegistryAuthProvider
{
    /// <summary>True when the provider targets Docker Hub (owner checks apply).</summary>
    bool IsDockerHub { get; }

    /// <summary>True when this provider should handle the given host/resource.</summary>
    bool Supports(string registryHost, DockerRegistryResource? resource);

    /// <summary>
    /// Returns the username/password (or token) for the host, preferring the
    /// bound resource over inline/fallback credentials.
    /// </summary>
    Task<(string username, string password)> GetLoginCredentialsAsync(
        string registryHost, DockerRegistryResource? resource,
        string fallbackUsername, string fallbackPassword);
}
