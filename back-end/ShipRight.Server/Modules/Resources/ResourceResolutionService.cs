using ShipRight.Modules.Projects;
using ShipRight.Modules.Resources.Stores;

namespace ShipRight.Modules.Resources;

public class ResourceResolutionService
{
    private readonly IDockerRegistryResourceStore _registryStore;
    private readonly IScriptResourceStore _scriptStore;

    public ResourceResolutionService(IDockerRegistryResourceStore registryStore, IScriptResourceStore scriptStore)
    {
        _registryStore = registryStore;
        _scriptStore = scriptStore;
    }

    public async Task<(string username, string password)> ResolveDockerCredentialsAsync(
        ServiceConfig service, string fallbackUsername, string fallbackPassword)
    {
        if (service.DockerRegistryResourceId is Guid resourceId)
        {
            var resource = await _registryStore.GetByIdAsync(resourceId);
            if (resource is not null)
                return (resource.Username, resource.Password);
        }

        var username = !string.IsNullOrEmpty(service.DockerUsername) ? service.DockerUsername : fallbackUsername;
        var password = !string.IsNullOrEmpty(service.DockerPassword) ? service.DockerPassword : fallbackPassword;
        return (username, password);
    }

    public async Task<string> ResolveRebuildScriptAsync(ServerConfig server)
    {
        if (server.RebuildScriptResourceId is Guid resourceId)
        {
            var resource = await _scriptStore.GetByIdAsync(resourceId);
            if (resource is not null)
                return resource.Content;
        }

        return server.RebuildScript;
    }
}
