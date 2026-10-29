using System.Text;
using System.Text.RegularExpressions;
using ShipRight.Modules.Projects;

namespace ShipRight.Modules.Setup;

/// <summary>
/// The docker-compose.yml ShipRight writes on a server that does not have one yet. Every image is
/// <c>${SERVICE_TAG:-image:latest}</c>: the EnvCompose deploy exports the tag of each service it just built, and the
/// fallback keeps services that were not part of this build running on their latest image.
/// </summary>
public static class GeneratedCompose
{
    public static readonly string[] FileNames = ["docker-compose.yml", "docker-compose.yaml", "compose.yml", "compose.yaml"];

    /// <summary>Null when the project has nothing to run.</summary>
    public static string? Render(ProjectConfig project)
    {
        if (project.Services.Count == 0) return null;

        var sb = new StringBuilder();
        sb.Append("# Written by ShipRight because this server had no compose file. It is yours now: edit it freely,\n");
        sb.Append("# ShipRight will never overwrite it. Image tags are filled in at every deploy.\n");
        sb.Append("services:\n");

        var usedHostPorts = new HashSet<int>();
        foreach (var s in project.Services)
        {
            sb.Append($"  {ComposeKey(s)}:\n");
            sb.Append($"    image: ${{{TagVariable(s.Name)}:-{s.DockerImageName}:latest}}\n");
            sb.Append("    restart: unless-stopped\n");
            if (s.ContainerPort is { } port)
            {
                var host = port;
                while (!usedHostPorts.Add(host)) host++;
                sb.Append("    ports:\n");
                sb.Append($"      - \"{host}:{port}\"\n");
            }
        }
        return sb.ToString();
    }

    /// <summary>Same rule the deploy uses to name the variable it exports.</summary>
    public static string TagVariable(string serviceName) =>
        Regex.Replace(serviceName.ToUpperInvariant(), @"[^A-Z0-9]", "_") + "_TAG";

    private static string ComposeKey(ServiceConfig s) =>
        !string.IsNullOrWhiteSpace(s.ComposeServiceName)
            ? s.ComposeServiceName
            : Regex.Replace(s.Name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-') is { Length: > 0 } slug ? slug : "app";
}
