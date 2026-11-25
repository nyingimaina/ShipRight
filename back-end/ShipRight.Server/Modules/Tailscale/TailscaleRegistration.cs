using Microsoft.Extensions.DependencyInjection;
using ShipRight.Modules.BuildRecipes;
using ShipRight.Modules.ServerNetwork;
using ShipRight.Modules.Tailscale;
using ShipRight.Shared.SshRunner;
using ShipRight.Shared.Tenancy;

namespace ShipRight.Modules.Tailscale;

public static class TailscaleRegistration
{
    public static IServiceCollection AddTailscaleNetworking(this IServiceCollection services, string dataDir)
    {
        services.AddSingleton<ITailnetStore>(sp =>
            new JsonTailnetStore(dataDir, sp.GetService<ICompanyContext>()));
        services.AddSingleton<TailscaleOAuthKeyMinter>();
        services.AddSingleton(sp => new TailnetManager(
            sp.GetRequiredService<ITailnetStore>(),
            sp.GetRequiredService<TailscaleOAuthKeyMinter>(),
            dataDir: dataDir));
        services.AddSingleton<ITailnetManager>(sp => sp.GetRequiredService<TailnetManager>());
        services.AddSingleton<DirectNetworkProvider>();
        services.AddSingleton<TailscaleNetworkProvider>();
        services.AddSingleton<IServerNetworkResolver, ServerNetworkResolver>();
        services.AddSingleton<Socks5SshRunner>();
        services.AddSingleton<IServerRoutedSshRunner>(sp => sp.GetRequiredService<Socks5SshRunner>());
        services.DecorateSingleton<ISshRunner>((sp, inner) => new NetworkAwareSshRunner(
            inner,
            sp.GetRequiredService<IServerNetworkResolver>(),
            sp.GetRequiredService<IServerRoutedSshRunner>()));
        services.AddSingleton<RawSshRunner>();
        services.AddSingleton<IRawSshRunner>(sp => sp.GetRequiredService<RawSshRunner>());
        services.AddSingleton<Socks5RawSshRunner>();
        services.AddSingleton<IServerRoutedRawSshRunner>(sp => sp.GetRequiredService<Socks5RawSshRunner>());
        services.DecorateSingleton<IRawSshRunner>((sp, inner) => new NetworkAwareRawSshRunner(
            inner,
            sp.GetRequiredService<IServerNetworkResolver>(),
            sp.GetRequiredService<IServerRoutedRawSshRunner>()));
        return services;
    }
}
