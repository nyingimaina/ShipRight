namespace ShipRight.Modules.JitDeploy;

/// <summary>One variable whose value differs between the server record and the project's copy of the server settings.</summary>
public sealed record EnvSideDifference(string Name, string ServerRecordValue, string ProjectValue);

/// <summary>
/// The two copies of the server's variables, with their values. Only produced by the explicit deploy-time check so the
/// deploy log and config summary keep their names-only rule.
/// </summary>
public sealed record EnvDivergence(
    bool Differs,
    IReadOnlyList<EnvSideDifference> Variables,
    string ServerRecordText,
    string ProjectText);

/// <summary>
/// Compares the server settings saved on the server record with the copy inside the project. JIT merges the project copy,
/// so a difference here can silently change what a deploy hands to compose.
/// </summary>
public static class JitEnvDivergence
{
    public static EnvDivergence Detect(string? projectEnv, string? serverRecordEnv)
    {
        var projectText = projectEnv ?? string.Empty;
        var serverText = serverRecordEnv ?? string.Empty;
        var differs = Normalize(projectText) != Normalize(serverText);

        var fromProject = JitEnvFile.ValuesOf(projectText);
        var fromServer = JitEnvFile.ValuesOf(serverText);
        var names = fromProject.Keys.Concat(fromServer.Keys)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);

        var variables = names
            .Select(n => new EnvSideDifference(n, fromServer.GetValueOrDefault(n, string.Empty), fromProject.GetValueOrDefault(n, string.Empty)))
            .Where(v => v.ServerRecordValue != v.ProjectValue)
            .ToList();

        return new EnvDivergence(differs, variables, serverText, projectText);
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');
}
