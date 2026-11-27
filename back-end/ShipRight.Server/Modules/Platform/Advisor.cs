using ShipRight.Modules.JitDeploy;
using ShipRight.Modules.Projects;

namespace ShipRight.Modules.Platform;

public sealed record AdvisorRequest(ProjectConfig Project, JitDeployConfig? Jit, string? Environment = null);

/// <summary>A proposal, never a change. It says what it would do and why, and nothing has run.</summary>
public sealed record DeploymentProposal(
    string ProjectId,
    string MethodId,
    IReadOnlyList<string> Services,
    IReadOnlyList<string> Notes,
    string Source,
    DateTimeOffset CreatedAt);

public interface IDeploymentAdvisor
{
    Task<DeploymentProposal?> ProposeAsync(AdvisorRequest request, CancellationToken ct = default);
}

public interface IShadowProposalStore
{
    Task SaveAsync(DeploymentProposal proposal, CancellationToken ct = default);
    Task<IReadOnlyList<DeploymentProposal>> GetAllAsync(CancellationToken ct = default);
}

public sealed class InMemoryShadowProposalStore : IShadowProposalStore
{
    private readonly List<DeploymentProposal> _proposals = [];
    private readonly object _gate = new();

    public Task SaveAsync(DeploymentProposal proposal, CancellationToken ct = default)
    {
        lock (_gate) _proposals.Add(proposal);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<DeploymentProposal>> GetAllAsync(CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<DeploymentProposal>>(_proposals.ToList());
    }
}

/// <summary>
/// A proposal from facts alone: no model, no network, no cost. It is the baseline the AI layer must beat, and it works
/// with AI switched off.
/// </summary>
public sealed class DeterministicAdvisor : IDeploymentAdvisor
{
    public Task<DeploymentProposal?> ProposeAsync(AdvisorRequest request, CancellationToken ct = default)
    {
        var project = request.Project;
        var jit = request.Jit;
        var services = new List<string>();
        var notes = new List<string>();

        if (jit is { Enabled: true, ComposeServices.Count: > 0 })
        {
            services.AddRange(jit.ComposeServices
                .Select(service => service.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name)));
            notes.Add("The compose file already names the services to build.");
        }
        else if (project.Services.Count > 0)
        {
            services.AddRange(project.Services
                .Select(service => service.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name)));
            notes.Add("The project lists the services to build.");
        }
        else
        {
            notes.Add("No services were found yet; add the compose file or list them.");
        }

        if (string.IsNullOrWhiteSpace(project.Server.Host))
            notes.Add("Add the server's address.");
        if (jit is not { Enabled: true })
            notes.Add("Build on the server is not turned on.");

        return Task.FromResult<DeploymentProposal?>(new DeploymentProposal(
            project.Id, "jit", services, notes, "deterministic", DateTimeOffset.UtcNow));
    }
}

/// <summary>
/// Runs an advisor beside the real path and records what it would have said. It is gated by a feature flag, defaults
/// to off, and never writes to a project, a server or a deployment.
/// </summary>
public sealed class ShadowAdvisor(
    IDeploymentAdvisor inner,
    IShadowProposalStore store,
    IFeatureFlagStore flags)
{
    public const string FlagKey = "advisor.shadow";

    public async Task<DeploymentProposal?> ProposeAsync(
        AdvisorRequest request, FlagContext context, CancellationToken ct = default)
    {
        if (!await flags.IsEnabledAsync(FlagKey, context, ct)) return null;

        var proposal = await inner.ProposeAsync(request, ct);
        if (proposal is not null) await store.SaveAsync(proposal, ct);
        return proposal;
    }
}
