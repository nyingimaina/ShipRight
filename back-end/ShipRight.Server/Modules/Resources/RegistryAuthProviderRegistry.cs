using ShipRight.Modules.Resources.Models;
using ShipRight.Modules.Resources.Stores;
using ShipRight.Shared.ProcessRunner;

namespace ShipRight.Modules.Resources;

/// <summary>
/// Selects the IRegistryAuthProvider for a registry host/resource pair.
/// Providers are consulted in registration order; the first match wins.
/// </summary>
public sealed class RegistryAuthProviderRegistry
{
    private readonly IReadOnlyList<IRegistryAuthProvider> _providers;

    public RegistryAuthProviderRegistry(IEnumerable<IRegistryAuthProvider> providers)
    {
        _providers = providers.ToList();
        if (_providers.Count == 0)
            throw new ArgumentException("At least one registry auth provider is required.", nameof(providers));
    }

    public IRegistryAuthProvider GetFor(string registryHost, DockerRegistryResource? resource) =>
        _providers.FirstOrDefault(p => p.Supports(registryHost, resource)) ?? _providers[0];

    public static RegistryAuthProviderRegistry CreateDefault(
        IProcessRunner? runner = null, IAwsProfileResourceStore? profileStore = null) =>
        new([new DockerHubAuthProvider(), new AwsEcrAuthProvider(runner, profileStore), new StandardAuthProvider()]);
}
