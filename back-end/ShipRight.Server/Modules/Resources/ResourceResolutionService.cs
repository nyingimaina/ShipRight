using ShipRight.Modules.Projects;
using ShipRight.Modules.Resources.Models;
using ShipRight.Modules.Resources.Stores;
using ShipRight.Shared.CommandExecution;
using ShipRight.Shared.ProcessRunner;

namespace ShipRight.Modules.Resources;

public class ResourceResolutionService
{
    private readonly IDockerRegistryResourceStore _registryStore;
    private readonly IScriptResourceStore _scriptStore;
    private readonly RegistryAuthProviderRegistry _providers;

    public ResourceResolutionService(IDockerRegistryResourceStore registryStore, IScriptResourceStore scriptStore,
        RegistryAuthProviderRegistry? providers = null, IProcessRunner? processRunner = null,
        IAwsProfileResourceStore? profileStore = null, ICommandExecutor? executor = null)
    {
        _registryStore = registryStore;
        _scriptStore = scriptStore;
        _providers = providers ?? RegistryAuthProviderRegistry.CreateDefault(processRunner, profileStore, executor);
    }

    public async Task<DockerRegistryResource?> ResolveRegistryResourceAsync(ServiceConfig service)
    {
        if (service.DockerRegistryResourceId is Guid resourceId)
            return await _registryStore.GetByIdAsync(resourceId);
        return null;
    }

    public async Task<(string username, string password)> ResolveDockerCredentialsAsync(
        ServiceConfig service, string fallbackUsername, string fallbackPassword)
    {
        var resource = await ResolveRegistryResourceAsync(service);
        var host = RegistryHostResolver.Resolve(service, resource);
        var provider = _providers.GetFor(host, resource);

        var inlineUser = !string.IsNullOrEmpty(service.DockerUsername) ? service.DockerUsername : fallbackUsername;
        var inlinePass = !string.IsNullOrEmpty(service.DockerPassword) ? service.DockerPassword : fallbackPassword;
        return await provider.GetLoginCredentialsAsync(host, resource, inlineUser, inlinePass);
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
