using ShipRight.Modules.Projects;
using ShipRight.Modules.Resources.Models;

namespace ShipRight.Modules.Resources;

/// <summary>
/// Resolves the registry host for a service.
/// Precedence: explicit DockerRegistry field → bound resource Registry → image prefix.
/// Defaults to "docker.io".
/// The single-argument overload preserves the original BuildOrchestrator.ResolveRegistry behavior.
/// </summary>
internal static class RegistryHostResolver
{
    public static bool IsDockerHub(string registry) =>
        registry == "docker.io" || registry == "index.docker.io";

    public static string Resolve(ServiceConfig svc) =>
        Resolve(svc, null);

    public static string Resolve(ServiceConfig svc, DockerRegistryResource? resource)
    {
        if (!string.IsNullOrEmpty(svc.DockerRegistry))
            return svc.DockerRegistry;
        if (resource is not null && !string.IsNullOrEmpty(resource.Registry))
            return resource.Registry;
        var image = svc.DockerImageName;
        if (string.IsNullOrEmpty(image)) return "docker.io";
        var firstSlash = image.IndexOf('/');
        if (firstSlash < 0) return "docker.io";
        var prefix = image[..firstSlash];
        return (prefix.Contains('.') || prefix.Contains(':')) ? prefix : "docker.io";
    }
}
