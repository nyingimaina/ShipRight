using ShipRight.Modules.Projects;
using ShipRight.Modules.Resources.Models;

namespace ShipRight.Modules.Transfer;

/// <summary>
/// Recreates a bundled project beside whatever already exists. Every record gets a fresh id and every
/// reference between them is rewritten to match, so an import can never overwrite or re-point
/// existing data. The whole bundle is parsed before the first write, so a bad bundle changes nothing.
/// </summary>
public class ProjectBundleImporter(TransferStores stores)
{
    public async Task<ImportOutcome> ImportAsync(byte[] bytes)
    {
        var bundle = ProjectBundleReader.Read(bytes);
        var now = DateTime.UtcNow;

        var name = await UniqueNameAsync(bundle.Project.Name);
        var projectId = await ProjectRouter.GenerateUniqueIdAsync(name, stores.Projects);

        var aws = bundle.AwsProfiles.ToDictionary(r => r.Id, _ => Guid.NewGuid());
        var registries = bundle.Registries.ToDictionary(r => r.Id, _ => Guid.NewGuid());
        var credentials = bundle.Credentials.ToDictionary(r => r.Id, _ => Guid.NewGuid());
        var scripts = bundle.Scripts.ToDictionary(r => r.Id, _ => Guid.NewGuid());
        var pipelines = bundle.Pipelines.ToDictionary(r => r.Id, _ => Guid.NewGuid());

        // A reference to something the bundle does not carry (it was already dangling at export) becomes empty.
        static Guid? Map(Dictionary<Guid, Guid> map, Guid? old) =>
            old is { } id && map.TryGetValue(id, out var fresh) ? fresh : null;
        string? Scope(string? oldProjectId) => oldProjectId is null ? null : projectId;

        foreach (var r in bundle.AwsProfiles)
            await stores.AwsProfiles.SaveAsync(r with { Id = aws[r.Id], CreatedAt = now, ModifiedAt = now });
        foreach (var r in bundle.Registries)
            await stores.Registries.SaveAsync(r with
            {
                Id = registries[r.Id], AwsProfileResourceId = Map(aws, r.AwsProfileResourceId), CreatedAt = now, ModifiedAt = now,
            });
        foreach (var r in bundle.Credentials)
            await stores.Credentials.SaveAsync(r with { Id = credentials[r.Id], ProjectId = Scope(r.ProjectId), CreatedAt = now, ModifiedAt = now });
        foreach (var r in bundle.Scripts)
            await stores.Scripts.SaveAsync(r with { Id = scripts[r.Id], ProjectId = Scope(r.ProjectId), CreatedAt = now, ModifiedAt = now });
        foreach (var r in bundle.Pipelines)
            await stores.Pipelines.SaveAsync(r with
            {
                Id = pipelines[r.Id], ProjectId = Scope(r.ProjectId), CreatedAt = now, ModifiedAt = now,
                Steps = r.Steps.Select(s => s with { Id = Guid.NewGuid(), ScriptResourceId = Map(scripts, s.ScriptResourceId) }).ToList(),
            });

        var serverId = string.Empty;
        ServerConfig? server = null;
        if (bundle.Server is not null)
        {
            serverId = $"srv-{Guid.NewGuid():N}"[..12];
            server = bundle.Server with
            {
                Id = serverId,
                RebuildScriptResourceId = Map(scripts, bundle.Server.RebuildScriptResourceId),
                PipelineResourceId = Map(pipelines, bundle.Server.PipelineResourceId),
                // The managed key never travels, so this machine has not authorized anything yet.
                ManagedSshKeyAuthorized = false,
            };
            await stores.Servers.SaveAsync(server);
        }

        var project = bundle.Project with
        {
            Id = projectId,
            Name = name,
            ServerId = serverId,
            Server = server ?? bundle.Project.Server,
            CreatedAt = now,
            ModifiedAt = now,
            Services = bundle.Project.Services
                .Select(s => s with { DockerRegistryResourceId = Map(registries, s.DockerRegistryResourceId) })
                .ToList(),
            GitRepos = bundle.Project.GitRepos
                .Select(g => g with { CredentialResourceId = Map(credentials, g.CredentialResourceId) })
                .ToList(),
        };
        await stores.Projects.SaveAsync(project);

        return new ImportOutcome(projectId, name, bundle.Manifest.Warnings);
    }

    private async Task<string> UniqueNameAsync(string name)
    {
        if (await stores.Projects.GetByNameAsync(name) is null) return name;
        var candidate = $"{name} (imported)";
        for (var i = 2; await stores.Projects.GetByNameAsync(candidate) is not null; i++)
            candidate = $"{name} (imported {i})";
        return candidate;
    }
}
