namespace ShipRight.Modules.Builds;

/// <summary>
/// Builds exact docker CLI argument arrays for registry operations.
/// Behavior preserved from BuildOrchestrator's original inline construction:
/// Docker Hub (docker.io / index.docker.io) registries omit the registry arg.
/// </summary>
internal static class DockerCommandBuilder
{
    public static bool IsDockerHub(string registry) =>
        registry == "docker.io" || registry == "index.docker.io";

    public static string[] BuildLogoutArgs(string registry) =>
        IsDockerHub(registry)
            ? ["logout"]
            : ["logout", registry];

    public static string[] BuildLoginArgs(string registry, string username)
    {
        var args = new List<string> { "login" };
        if (!IsDockerHub(registry))
            args.Add(registry);
        args.AddRange(["-u", username, "--password-stdin"]);
        return args.ToArray();
    }

    public static string[] BuildPushArgs(string image, string version) =>
        ["push", $"{image}:{version}"];
}
