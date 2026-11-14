namespace ShipRight.Modules.JitDeploy;

/// <summary>Plain-language help for build failures that are the server's tooling, not the project's files.</summary>
public static class JitBuildAdvice
{
    /// <summary>An empty string when there is nothing to add.</summary>
    public static string For(IEnumerable<string> buildOutput)
    {
        var text = string.Join("\n", buildOutput);
        var buildKit = text.Contains("requires BuildKit", StringComparison.OrdinalIgnoreCase)
                       || text.Contains("BuildKit is enabled but the buildx component is missing", StringComparison.OrdinalIgnoreCase);
        return buildKit
            ? " This is not a problem in your Dockerfile: it uses a BuildKit feature, and Docker on the server is building without BuildKit. " +
              "ShipRight installs the missing piece (buildx) at the start of a deploy, so Deploy again. If it still fails, run 'docker buildx version' on the server and check that it prints a version."
            : string.Empty;
    }
}
