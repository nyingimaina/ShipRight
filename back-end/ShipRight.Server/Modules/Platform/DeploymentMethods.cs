using ShipRight.Modules.JitDeploy;
using ShipRight.Modules.Projects;

namespace ShipRight.Modules.Platform;

/// <summary>What a method would do, and what stops it. It never deploys; it only plans.</summary>
public sealed record DeploymentMethodPlan(
    string MethodId,
    bool Ready,
    IReadOnlyList<string> Problems,
    IReadOnlyList<string> Steps);

public sealed record DeploymentMethodInfo(string Id, string Title, bool Available);

public interface IDeploymentMethod
{
    string Id { get; }
    string Title { get; }
    bool CanHandle(ProjectConfig project);
    Task<DeploymentMethodPlan> PlanAsync(ProjectConfig project, CancellationToken ct = default);
}

public interface IDeploymentMethodRegistry
{
    IReadOnlyList<DeploymentMethodInfo> All { get; }
    IDeploymentMethod? Find(string id);
}

public sealed class DeploymentMethodRegistry(IEnumerable<IDeploymentMethod> methods) : IDeploymentMethodRegistry
{
    private readonly IReadOnlyList<IDeploymentMethod> _methods = methods.ToList();

    public IReadOnlyList<DeploymentMethodInfo> All =>
        _methods.Select(method => new DeploymentMethodInfo(method.Id, method.Title, true)).ToList();

    public IDeploymentMethod? Find(string id) =>
        _methods.FirstOrDefault(method => method.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Wraps the existing build-on-server engine. It deliberately calls the same mapper the deploy uses, so the plan shown
/// here can never drift from the plan that runs.
/// </summary>
public sealed class JitDeploymentMethod(IJitConfigStore configs) : IDeploymentMethod
{
    public string Id => "jit";
    public string Title => "Build on the server";

    public bool CanHandle(ProjectConfig project) => project.Type == ProjectType.Pipeline;

    public async Task<DeploymentMethodPlan> PlanAsync(ProjectConfig project, CancellationToken ct = default)
    {
        var config = await configs.GetAsync(project.Id);
        var mapped = JitDeployMapper.Map(project, config);

        IReadOnlyList<string> steps = mapped.Ok
            ?
            [
                "Fetch the exact commit on the server",
                "Build the services the compose file names",
                "Swap with a health check and keep the previous release for rollback",
            ]
            : [];

        return new DeploymentMethodPlan(Id, mapped.Ok, mapped.Problems, steps);
    }
}

/// <summary>The existing build-machine pipeline, kept working behind the same contract.</summary>
public sealed class LegacyDeploymentMethod : IDeploymentMethod
{
    public string Id => "legacy";
    public string Title => "Existing pipeline";

    public bool CanHandle(ProjectConfig project) => true;

    public Task<DeploymentMethodPlan> PlanAsync(ProjectConfig project, CancellationToken ct = default)
    {
        var problems = new List<string>();
        var server = project.Server;

        if (string.IsNullOrWhiteSpace(server.Host)) problems.Add("Choose the server this project deploys to.");
        if (string.IsNullOrWhiteSpace(server.Username)) problems.Add("Add the user ShipRight signs in as.");
        if (string.IsNullOrWhiteSpace(server.SshKeyPath)) problems.Add("Add an SSH key for the server.");
        if (string.IsNullOrWhiteSpace(server.RemoteWorkingDir)) problems.Add("Choose the folder on the server.");

        var ready = problems.Count == 0;
        IReadOnlyList<string> steps = ready ? ["Build", "Push", "Deploy"] : [];

        return Task.FromResult(new DeploymentMethodPlan(Id, ready, problems, steps));
    }
}
