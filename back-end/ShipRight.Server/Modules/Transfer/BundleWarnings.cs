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
        for (var i = 0; i < p.GitRepos.Count; i++)
            foreach (var w in Path($"git repo {i + 1}", "repository path", p.GitRepos[i].RepoPath)) yield return w;
        foreach (var w in Path("project", "compose working directory", p.Wsl.WorkingDir)) yield return w;
        if (BundleSecrets.HasUrlCredentials(p.Wsl.ComposeRepoUrl)) yield return Secret("project", "compose repository URL token or password");
        if (p.Database is not null)
            foreach (var w in Secret("database", "root password", p.Database.RootPassword)) yield return w;
    }

    public static IEnumerable<BundleWarning> ForServer(ServerConfig s)
    {
        var at = $"server '{s.Name}'";
        foreach (var w in Path(at, "SSH key file", s.SshKeyPath)) yield return w;
        if (BundleSecrets.HasUrlCredentials(s.ComposeRepoUrl)) yield return Secret(at, "compose repository URL token or password");
        if (s.ManagedSshKey)
            yield return new BundleWarning
            {
                Code = "ssh-key-not-transferred", Location = at,
                Message = $"{at} used a ShipRight-managed SSH key. Its path is portable; authorize the restored key after importing if the server is the same.",
            };
        if (!string.IsNullOrWhiteSpace(s.RemoteWorkingDir))
            yield return new BundleWarning
            {
                Severity = "info", Code = "remote-path", Location = at,
                Message = $"{at} keeps its deployment directory on the remote host. Check it points at the intended stack on the destination server.",
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

    public static IEnumerable<BundleWarning> ForScript(ScriptResource s)
    {
        var at = $"script '{s.Name}'";
        foreach (var w in Path(at, "working directory", s.WorkingDirectory)) yield return w;
        if (s.Variables.Values.Any(v => !string.IsNullOrEmpty(v)))
            yield return Secret(at, "variables");
    }

    public static IEnumerable<BundleWarning> ForPipeline(PipelineResource p)
    {
        var at = $"pipeline '{p.Name}'";
        foreach (var step in p.Steps)
            foreach (var w in Path(at, "step working directory", step.WorkingDirectory)) yield return w;
        if (p.Variables.Values.Any(v => !string.IsNullOrEmpty(v)))
            yield return Secret(at, "variables");
    }

    private static IEnumerable<BundleWarning> Path(string at, string what, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) yield break;
        yield return new BundleWarning
        {
            Code = "path", Location = at,
            Message = $"{at}: {what} was a location on the original machine and is now portable. Choose the destination root when importing.",
        };
    }

    private static BundleWarning Secret(string at, string what) => new()
    {
        Code = "secret-omitted", Location = at,
        Message = $"{at}: the {what} is never exported. Enter it again after importing.",
    };

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
