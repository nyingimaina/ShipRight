namespace ShipRight.Modules.JitDeploy;

/// <summary>A variable defined on both the server and the project, with the value each side supplies.</summary>
public sealed record EnvDuplicate(string Name, string ServerValue, string ProjectValue);

/// <summary>
/// Detects variables the server and the project both define, so the deploy-time warning can show what each side has.
/// Removing a name from one side is a pure text transform; which side is saved is decided by the caller.
/// </summary>
public static class JitEnvDuplicates
{
    public static IReadOnlyList<EnvDuplicate> Detect(string? server, string? project)
    {
        var fromServer = JitEnvFile.ValuesOf(server);
        var fromProject = JitEnvFile.ValuesOf(project);
        return fromServer.Keys.Where(fromProject.ContainsKey)
            .Select(name => new EnvDuplicate(name, fromServer[name], fromProject[name]))
            .ToList();
    }

    public static string Remove(string? text, IEnumerable<string> names) => JitEnvFile.Without(text, names);
}
