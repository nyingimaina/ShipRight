using System.Text.RegularExpressions;
using ShipRight.Modules.Projects;
using ShipRight.Modules.Setup;

namespace ShipRight.Modules.JitDeploy;

public sealed record JitMapResult(JitDeployRequest? Request, IReadOnlyList<string> Problems)
{
    public bool Ok => Request is not null;
}

/// <summary>Turns a project plus its build-on-server settings into a deploy request, or says in plain words what is missing.</summary>
public static class JitDeployMapper
{
    public static JitMapResult Map(ProjectConfig project, JitDeployConfig? cfg, string? commit = null, bool force = false, bool allowWithoutRollback = false)
    {
        if (cfg is null || !cfg.Enabled)
            return new(null, ["Building on the server is not turned on for this project."]);

        var problems = new List<string>();
        var server = project.Server;
        if (string.IsNullOrWhiteSpace(server.Host) || string.IsNullOrWhiteSpace(server.Username) ||
            string.IsNullOrWhiteSpace(server.SshKeyPath) || string.IsNullOrWhiteSpace(server.RemoteWorkingDir))
            problems.Add("Choose the server this project deploys to (its address, user, SSH key and folder).");
        if (string.IsNullOrWhiteSpace(cfg.RepoUrl))
            problems.Add("Enter the git address the server should fetch your code from.");
        else if (GitAddressForms.Analyse(cfg.RepoUrl).HadCredentials)
            problems.Add("The saved git address contains a password or token. ShipRight does not keep secrets inside an address. Use the SSH form (git@host:owner/repo.git) with a deploy key.");
        if (project.Services.Count == 0)
            problems.Add("This project has no services to deploy.");

        var services = new List<JitService>();
        foreach (var s in project.Services)
        {
            var image = (s.DockerImageName ?? string.Empty).Trim();
            if (image.Length == 0) { problems.Add($"Service '{s.Name}' has no image name."); continue; }
            if (image != image.ToLowerInvariant())
            {
                problems.Add($"Service '{s.Name}' has capital letters in its image name ('{image}'); Docker needs lower-case image names.");
                continue;
            }
            services.Add(new JitService(ComposeServiceName(s), GeneratedCompose.TagVariable(s.Name), image));
        }

        if (problems.Count > 0) return new(null, problems);

        var request = new JitDeployRequest
        {
            Target = new JitTarget(server.Host, server.Username, server.SshKeyPath, server.RemoteWorkingDir),
            Project = ComposeProjectName(project, cfg),
            RepoUrl = cfg.RepoUrl,
            Branch = cfg.Branch,
            Commit = string.IsNullOrWhiteSpace(commit) ? null : commit,
            EnvFile = string.IsNullOrWhiteSpace(cfg.EnvFile) ? null : cfg.EnvFile,
            ComposeFile = string.IsNullOrWhiteSpace(cfg.ComposeFile) ? null : cfg.ComposeFile.Trim(),
            DeployKeyName = cfg.UseDeployKey ? JitDeployKey.KeyName(ComposeProjectName(project, cfg)) : null,
            Services = services,
            Force = force,
            AllowWithoutRollback = allowWithoutRollback,
            Settings = new JitDeploySettings
            {
                ReleasesToKeep = cfg.ReleasesToKeep,
                StopGraceSeconds = cfg.StopGraceSeconds,
                HealthTimeoutSeconds = cfg.HealthTimeoutSeconds,
                SettleSeconds = cfg.SettleSeconds,
                MinFreeDiskMb = cfg.MinFreeDiskMb,
                BuildNice = cfg.BuildNice,
            },
            DependentChoices = (cfg.DependentChoices ?? [])
                .GroupBy(c => (c.Service, c.Dependent))
                .ToDictionary(g => g.Key, g => g.Last().Restart),
        };
        return new(request, []);
    }

    /// <summary>The services a deploy would build, as they would be named in compose. Used to show the user what their compose file must contain.</summary>
    public static IReadOnlyList<JitService> Services(ProjectConfig project) =>
        project.Services
            .Where(s => !string.IsNullOrWhiteSpace(s.DockerImageName))
            .Select(s => new JitService(ComposeServiceName(s), GeneratedCompose.TagVariable(s.Name), s.DockerImageName.Trim()))
            .ToList();

    /// <summary>Where the project lives on its server, or null when no server is chosen yet.</summary>
    public static JitTarget? TargetOf(ProjectConfig project)
    {
        var s = project.Server;
        if (string.IsNullOrWhiteSpace(s.Host) || string.IsNullOrWhiteSpace(s.Username) ||
            string.IsNullOrWhiteSpace(s.SshKeyPath) || string.IsNullOrWhiteSpace(s.RemoteWorkingDir))
            return null;
        return new JitTarget(s.Host, s.Username, s.SshKeyPath, s.RemoteWorkingDir);
    }

    /// <summary>The same rule the generated compose file uses to name a service.</summary>
    public static string ComposeServiceName(ServiceConfig s) =>
        !string.IsNullOrWhiteSpace(s.ComposeServiceName)
            ? s.ComposeServiceName
            : Slug(s.Name, "[^a-z0-9]+", "app");

    public static string ComposeProjectName(ProjectConfig project, JitDeployConfig cfg) =>
        !string.IsNullOrWhiteSpace(cfg.ComposeProject) ? cfg.ComposeProject : Slug(project.Name, "[^a-z0-9]+", "app");

    private static string Slug(string value, string pattern, string fallback)
    {
        var slug = Regex.Replace((value ?? string.Empty).ToLowerInvariant(), pattern, "-").Trim('-');
        return slug.Length > 0 ? slug : fallback;
    }
}
