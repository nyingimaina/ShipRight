using ShipRight.Modules.Projects;

namespace ShipRight.Modules.RemoteHost;

/// <summary>
/// ECR login with a time limit. On a starved server `aws` / `docker login` can stall for minutes with no output;
/// this turns that into a clear failure instead of a silent hang.
/// </summary>
public static class RemoteEcrLogin
{
    public const int TimeoutSeconds = 90;

    public static string? BuildWithTimeout(ServerConfig server)
    {
        var login = RemoteServerBootstrapper.BuildEcrLoginCommand(server);
        if (login is null) return null;
        return $"timeout {TimeoutSeconds} sh -c '{login}'; rc=$?; " +
               "if [ $rc -eq 124 ]; then " +
               $"echo 'ECR login timed out after {TimeoutSeconds}s. The server may be out of memory (see the Stats tab) " +
               "or unable to reach AWS (check the instance role and network).'; fi; exit $rc";
    }
}
