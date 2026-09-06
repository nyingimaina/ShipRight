using ShipRight.Modules.Resources.Models;

namespace ShipRight.Modules.Resources;

public sealed class DockerHubAuthProvider : IRegistryAuthProvider
{
    public bool IsDockerHub => true;

    public bool Supports(string registryHost, DockerRegistryResource? resource) =>
        RegistryHostResolver.IsDockerHub(registryHost);

    public Task<(string username, string password)> GetLoginCredentialsAsync(
        string registryHost, DockerRegistryResource? resource,
        string fallbackUsername, string fallbackPassword)
    {
        if (resource is not null)
            return Task.FromResult((resource.Username, resource.Password));
        return Task.FromResult((fallbackUsername, fallbackPassword));
    }
}
