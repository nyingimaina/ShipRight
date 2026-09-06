using ShipRight.Modules.Resources.Models;

namespace ShipRight.Modules.Resources;

/// <summary>
/// Catch-all provider for any non-Docker Hub registry (GHCR, ACR, self-hosted, ...).
/// Credentials come from the bound resource or inline/fallback values.
/// </summary>
public sealed class StandardAuthProvider : IRegistryAuthProvider
{
    public bool IsDockerHub => false;

    public bool Supports(string registryHost, DockerRegistryResource? resource) => true;

    public Task<(string username, string password)> GetLoginCredentialsAsync(
        string registryHost, DockerRegistryResource? resource,
        string fallbackUsername, string fallbackPassword)
    {
        if (resource is not null)
            return Task.FromResult((resource.Username, resource.Password));
        return Task.FromResult((fallbackUsername, fallbackPassword));
    }
}
