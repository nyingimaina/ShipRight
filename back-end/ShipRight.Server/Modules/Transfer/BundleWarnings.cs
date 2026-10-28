using ShipRight.Modules.Projects;
using ShipRight.Modules.Resources.Models;

namespace ShipRight.Modules.Transfer;

/// <summary>
/// Works out what a person has to re-check after a project moves to another machine. Run against the
/// original records at export time, so it can say which secrets existed before they were blanked.
/// </summary>
internal static class BundleWarnings
{
    public static IEnumerable<BundleWarning> ForProject(ProjectConfig p)
    {
        foreach (var s in p.Services)
        {
            var at = $"service '{s.Name}'";
            foreach (var w in Path(at, "version file", s.VersionFilePath)) yield return w;
            foreach (var w in Path(at, "build context", s.BuildContextPath)) yield return w;
            foreach (var w in Secret(at, "Docker password", s.DockerPassword)) yield return w;
        }
        foreach (var g in p.GitRepos)
            foreach (var w in Path($"git repo '{g.RepoPath}'", "repository path", g.RepoPath)) yield return w;
        foreach (var w in Path("project", "compose working directory", p.Wsl.WorkingDir)) yield return w;
        if (p.Database is not null)
            foreach (var w in Secret("database", "root password", p.Database.RootPassword)) yield return w;
    }

    public static IEnumerable<BundleWarning> ForServer(ServerConfig s)
    {
        var at = $"server '{s.Name}'";
        foreach (var w in Path(at, "SSH key file", s.SshKeyPath)) yield return w;
        if (s.ManagedSshKey)
            yield return new BundleWarning
            {
                Code = "ssh-key-not-transferred", Location = at,
                Message = $"{at} used a ShipRight-managed SSH key. The key itself is never exported — generate and authorize a new one on this machine.",
            };
        if (!string.IsNullOrWhiteSpace(s.RemoteWorkingDir))
            yield return new BundleWarning
            {
                Severity = "info", Code = "remote-path", Location = at,
                Message = $"{at} deploys into '{s.RemoteWorkingDir}' on the remote machine — fine if that machine is unchanged.",
            };
        foreach (var w in Secret(at, ".env file", s.EnvFile)) yield return w;
    }

    public static IEnumerable<BundleWarning> ForRegistry(DockerRegistryResource r) => Secret($"registry '{r.Name}'", "password", r.Password);

    public static IEnumerable<BundleWarning> ForAwsProfile(AwsProfileResource a)
    {
        var at = $"AWS profile '{a.Name}'";
        foreach (var w in Secret(at, "access keys", a.SecretAccessKey + a.AccessKeyId + a.SessionToken)) yield return w;
        if (!string.IsNullOrEmpty(a.ProfileName))
            yield return new BundleWarning
            {
                Code = "aws-profile-name", Location = at,
                Message = $"{at} refers to the profile '{a.ProfileName}' in the AWS config of the original machine. Make sure this machine has a profile with that name.",
            };
    }

    public static IEnumerable<BundleWarning> ForCredential(CredentialResource c) => Secret($"credential '{c.Name}'", "value", c.Value);

    public static IEnumerable<BundleWarning> ForScript(ScriptResource s) => Path($"script '{s.Name}'", "working directory", s.WorkingDirectory);

    public static IEnumerable<BundleWarning> ForPipeline(PipelineResource p) =>
        p.Steps.SelectMany(step => Path($"pipeline '{p.Name}'", "step working directory", step.WorkingDirectory));

    private static IEnumerable<BundleWarning> Path(string at, string what, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) yield break;
        yield return new BundleWarning
        {
            Code = "path", Location = at,
            Message = $"{at}: {what} is '{value}' — a location on the original machine. Update it if this machine differs.",
        };
    }

    private static IEnumerable<BundleWarning> Secret(string at, string what, string? value)
    {
        if (string.IsNullOrEmpty(value)) yield break;
        yield return new BundleWarning
        {
            Code = "secret-omitted", Location = at,
            Message = $"{at}: the {what} is never exported. Enter it again after importing.",
        };
    }
}
