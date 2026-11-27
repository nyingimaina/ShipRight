using Microsoft.Extensions.DependencyInjection;

namespace ShipRight.Modules.Platform;

/// <summary>
/// Registers the spine in either mode. Everything is additive and dark: no route changes, no existing service is
/// replaced, and no flag is on.
/// </summary>
public static class PlatformRegistration
{
    public static IServiceCollection AddPlatformSpine(this IServiceCollection services)
    {
        services.AddSingleton<IFeatureFlagStore, InMemoryFeatureFlagStore>();
        services.AddSingleton<IRuleEngine, RuleEngine>();
        services.AddSingleton<IDeploymentMethod, JitDeploymentMethod>();
        services.AddSingleton<IDeploymentMethod, LegacyDeploymentMethod>();
        services.AddSingleton<IDeploymentMethodRegistry, DeploymentMethodRegistry>();
        services.AddSingleton<IShadowProposalStore, InMemoryShadowProposalStore>();
        services.AddSingleton<IDeploymentAdvisor, DeterministicAdvisor>();
        services.AddSingleton<ShadowAdvisor>();
        return services;
    }
}

public static class PlatformRoutes
{
    public static void MapPlatformRoutes(this WebApplication app)
    {
        // A read-only window on the spine: which methods exist and which flags are set. It exposes no secrets.
        app.MapGet("/api/platform/status", async (IDeploymentMethodRegistry methods, IFeatureFlagStore flags) =>
            Results.Ok(new
            {
                methods = methods.All,
                flags = await flags.GetAllAsync(),
            }));
    }
}
