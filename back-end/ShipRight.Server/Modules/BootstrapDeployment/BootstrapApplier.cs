using ShipRight.Modules.JitDeploy;
using ShipRight.Modules.Projects;
using ShipRight.Modules.Servers;

namespace ShipRight.Modules.BootstrapDeployment;

/// <summary>
/// Writes a reviewed plan: the server record, the project and the build-on-server settings. It reuses an existing
/// server with the same address and an existing project with the same id or name, so running the same bootstrap twice
/// updates instead of duplicating. A plan that has problems writes nothing.
/// </summary>
public sealed class BootstrapApplier(
    IProjectStore projects,
    IServerStore servers,
    IJitConfigStore jit,
    IBootstrapKeyProvisioner keys)
{
    public async Task<BootstrapApplyResult> ApplyAsync(BootstrapPlan plan, CancellationToken ct = default)
    {
        if (!plan.Ok)
            return new BootstrapApplyResult { Ok = false, Problems = plan.Problems };

        try
        {
            var server = await ResolveServerAsync(plan.Server, ct);

            string? publicKey = null;
            if (server.ManagedSshKey && string.IsNullOrWhiteSpace(server.SshKeyPath))
            {
                var key = await keys.EnsureAsync(server.Id, ct);
                server = server with
                {
                    SshKeyPath = key.PrivateKeyPath,
                    ManagedSshKey = true,
                    ManagedSshKeyAuthorized = false,
                };
                publicKey = key.PublicKey;
            }

            var now = DateTime.UtcNow;
            var project = await ResolveProjectAsync(plan.Project, server, now);

            await servers.SaveAsync(server);
            await projects.SaveAsync(project);
            await jit.SaveAsync(project.Id, plan.Jit);

            return new BootstrapApplyResult
            {
                Ok = true,
                ProjectId = project.Id,
                ServerId = server.Id,
                PublicKey = publicKey,
                NextSteps = NextSteps(project.Id, publicKey),
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            // A missing ssh-keygen, a read-only folder or a locked store is something a person can act on; it is not a 500.
            return new BootstrapApplyResult { Ok = false, Problems = [ex.Message] };
        }
    }

    private async Task<ServerConfig> ResolveServerAsync(ServerConfig planned, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var byId = string.IsNullOrWhiteSpace(planned.Id) ? null : await servers.GetByIdAsync(planned.Id);
        var byHost = (await servers.GetAllAsync())
            .FirstOrDefault(s => string.Equals(s.Host, planned.Host, StringComparison.OrdinalIgnoreCase));
        var existing = byHost ?? byId;

        if (existing is null)
            return string.IsNullOrWhiteSpace(planned.Id) ? planned with { Id = Guid.NewGuid().ToString() } : planned;

        // Keep what the existing record already learned (an authorized managed key, a manually chosen path) unless
        // this plan explicitly points somewhere else.
        return planned with
        {
            Id = existing.Id,
            SshKeyPath = string.IsNullOrWhiteSpace(planned.SshKeyPath) ? existing.SshKeyPath : planned.SshKeyPath,
            ManagedSshKey = planned.ManagedSshKey || existing.ManagedSshKey,
            ManagedSshKeyAuthorized = string.IsNullOrWhiteSpace(planned.SshKeyPath) && existing.ManagedSshKeyAuthorized,
        };
    }

    private async Task<ProjectConfig> ResolveProjectAsync(ProjectConfig planned, ServerConfig server, DateTime now)
    {
        var existing = !string.IsNullOrWhiteSpace(planned.Id) ? await projects.GetByIdAsync(planned.Id) : null;
        existing ??= await projects.GetByNameAsync(planned.Name);

        var project = existing is null
            ? planned with { Id = string.IsNullOrWhiteSpace(planned.Id) ? Guid.NewGuid().ToString() : planned.Id }
            : planned with { Id = existing.Id, CreatedAt = existing.CreatedAt };

        return project with
        {
            ServerId = server.Id,
            Server = server,
            CreatedAt = project.CreatedAt == default ? now : project.CreatedAt,
            ModifiedAt = now,
        };
    }

    private static IReadOnlyList<string> NextSteps(string projectId, string? publicKey)
    {
        var steps = new List<string>();
        if (publicKey is not null)
            steps.Add("Add the public key to the server's authorized keys, then run a dry run.");
        else
            steps.Add("Run a dry run to check the server without changing it.");
        steps.Add($"Then deploy: POST /api/jit/{projectId}/deploy.");
        return steps;
    }
}
