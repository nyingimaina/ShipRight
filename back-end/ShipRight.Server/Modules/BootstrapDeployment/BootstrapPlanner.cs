using System.Text.RegularExpressions;
using ShipRight.Modules.JitDeploy;
using ShipRight.Modules.Projects;
using ShipRight.Modules.Setup;

namespace ShipRight.Modules.BootstrapDeployment;

/// <summary>
/// Turns a request into a complete configuration without writing anything. It is deliberately pure: the same request
/// always produces the same plan, so the result can be reviewed, diffed and tested before any file or server changes.
/// </summary>
public static class BootstrapPlanner
{
    private static readonly Regex EnvName = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    public static BootstrapPlan Plan(BootstrapRequest raw)
    {
        var request = ShipRightSelfTemplate.FillDefaults(raw);
        var problems = new List<string>();
        var warnings = new List<string>();

        var name = (request.ProjectName ?? string.Empty).Trim();
        if (name.Length == 0) problems.Add("Give this deployment a name.");

        var repoUrl = (request.RepoUrl ?? string.Empty).Trim();
        if (repoUrl.Length == 0) problems.Add("Enter the git address the server should fetch your code from.");
        else Try(problems, "git address", () => JitNames.RepoUrl(repoUrl));

        var branch = (request.Branch ?? string.Empty).Trim();
        if (branch.Length == 0) problems.Add("Enter the branch to deploy.");
        else Try(problems, "branch name", () => JitNames.Branch(branch));

        var composeFile = (request.ComposeFile ?? string.Empty).Trim();
        if (composeFile.Length == 0) problems.Add("Choose the compose file inside the repository.");
        else Try(problems, "compose file", () => JitNames.RepoFile(composeFile));

        var services = PlanServices(request.Services, problems);
        if (services.Count == 0) problems.Add("Add at least one service to build.");

        var host = (request.Host ?? string.Empty).Trim();
        if (host.Length == 0) problems.Add("Enter the server's address.");

        var username = (request.Username ?? string.Empty).Trim();
        if (username.Length == 0) problems.Add("Enter the user ShipRight signs in as.");

        var workingDir = (request.RemoteWorkingDir ?? string.Empty).Trim();
        if (workingDir.Length == 0 && username.Length > 0 && name.Length > 0)
            workingDir = username == "root"
                ? $"/root/{Slug(name)}"
                : $"/home/{username}/{Slug(name)}";
        if (workingDir.Length == 0) problems.Add("Enter the folder on the server.");
        else Try(problems, "server folder", () => JitNames.Path(workingDir));

        var keyPath = (request.SshKeyPath ?? string.Empty).Trim();
        var managedKey = keyPath.Length == 0;

        var envText = RenderEnv(request.Env, warnings);

        var composeProject = string.IsNullOrWhiteSpace(request.ComposeProject)
            ? Slug(name)
            : request.ComposeProject.Trim();
        Try(problems, "compose project name", () => JitNames.Project(composeProject));

        if (request.Preset == BootstrapPreset.ShipRightSelf && string.IsNullOrWhiteSpace(request.Domain))
            problems.Add("Enter the domain people will visit (for example staging.example.com).");

        var serverId = string.IsNullOrWhiteSpace(request.ServerId) ? Guid.NewGuid().ToString() : request.ServerId.Trim();
        var projectId = string.IsNullOrWhiteSpace(request.ProjectId) ? Slug(name) : request.ProjectId.Trim();

        var server = new ServerConfig
        {
            Id = serverId,
            Name = string.IsNullOrWhiteSpace(request.ServerName) ? (host.Length > 0 ? host : "server") : request.ServerName.Trim(),
            Host = host,
            Username = username,
            SshKeyPath = keyPath,
            RemoteWorkingDir = workingDir,
            ManagedSshKey = managedKey,
            ManagedSshKeyAuthorized = false,
            HealthCheckUrl = (request.HealthCheckUrl ?? string.Empty).Trim(),
        };

        // Services deliberately live in the JIT settings, not in the project: a project with its own services must
        // point at a local checkout and WSL folder, which a bootstrap on a fresh machine does not have.
        var project = new ProjectConfig
        {
            Id = projectId,
            Name = name,
            ServerId = serverId,
            Server = server,
            Type = ProjectType.Pipeline,
            Services = [],
            GitRepos = [],
            Wsl = new WslConfig(),
        };

        var jit = new JitDeployConfig
        {
            Enabled = true,
            RepoUrl = repoUrl,
            Branch = branch,
            ComposeProject = composeProject,
            ComposeFile = composeFile,
            ComposeServices = services,
            EnvText = envText,
            UseDeployKey = request.UseDeployKey,
            CredentialResourceId = request.CredentialResourceId,
        };

        if (envText.Length > 0)
            warnings.Add("Environment values are written to the build-on-server settings in plain text. Treat this computer as trusted.");

        return new BootstrapPlan
        {
            Server = server,
            Project = project,
            Jit = jit,
            Problems = problems,
            Warnings = warnings,
            Steps = BuildSteps(managedKey),
        };
    }

    private static List<JitConfiguredService> PlanServices(
        IReadOnlyList<BootstrapService>? requested, List<string> problems)
    {
        var services = new List<JitConfiguredService>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var service in requested ?? [])
        {
            var name = (service.Name ?? string.Empty).Trim();
            if (name.Length == 0)
            {
                problems.Add("Every service needs a name.");
                continue;
            }

            if (!seen.Add(name))
            {
                problems.Add("Each service can only be listed once.");
                continue;
            }

            Try(problems, $"service '{name}' name", () => JitNames.Service(name));

            var image = (service.Image ?? string.Empty).Trim();
            if (image.Length == 0) problems.Add($"Service '{name}' needs an image name.");
            else Try(problems, $"service '{name}' image", () => JitNames.ImageRef(image));

            var dockerfile = (service.Dockerfile ?? string.Empty).Trim();
            if (dockerfile.Length == 0) problems.Add($"Pick the Dockerfile for '{name}'.");
            else if (dockerfile.StartsWith('/') || dockerfile.Split('/').Contains(".."))
                problems.Add($"The Dockerfile for '{name}' must be a path inside the repository.");

            var context = string.IsNullOrWhiteSpace(service.Context) ? "." : service.Context.Trim();
            services.Add(new JitConfiguredService(
                name,
                image,
                GeneratedCompose.TagVariable(name),
                dockerfile,
                context,
                string.IsNullOrWhiteSpace(service.Target) ? null : service.Target.Trim(),
                service.Args is { Count: > 0 }
                    ? new Dictionary<string, string>(service.Args, StringComparer.Ordinal)
                    : null));
        }

        return services;
    }

    private static string RenderEnv(IReadOnlyDictionary<string, string>? env, List<string> warnings)
    {
        if (env is null || env.Count == 0) return string.Empty;

        var lines = new List<string>();
        foreach (var (key, value) in env.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!EnvName.IsMatch(key))
            {
                warnings.Add($"Skipped an environment name that is not valid: '{key}'.");
                continue;
            }

            var text = value ?? string.Empty;
            if (text.Any(char.IsControl))
            {
                warnings.Add($"Skipped '{key}' because its value contains a control character.");
                continue;
            }

            lines.Add($"{key}={text}");
        }

        return string.Join("\n", lines);
    }

    private static IReadOnlyList<BootstrapStep> BuildSteps(bool managedKey)
    {
        var steps = new List<BootstrapStep>
        {
            new("save-server", "Save the server", "ShipRight records where the app will run."),
        };

        if (managedKey)
        {
            steps.Add(new("generate-key", "Make a key", "ShipRight creates a key for this server."));
            steps.Add(new("authorize-key", "Authorize the key", "Add the public key to the server once."));
        }

        steps.Add(new("save-project", "Write the project", "ShipRight writes the project and deployment settings from scratch."));
        steps.Add(new("dry-run", "Dry run", "Check the plan against the server without changing it."));
        steps.Add(new("deploy", "Deploy", "Build and start on the server."));
        return steps;
    }

    private static void Try(List<string> problems, string what, Func<string> check)
    {
        try { check(); }
        catch (ArgumentException ex) { problems.Add($"The {what} is not valid. {ex.Message}"); }
    }

    private static string Slug(string value) =>
        Regex.Replace((value ?? string.Empty).ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-') is { Length: > 0 } slug
            ? slug
            : "app";
}
