using ShipRight.Modules.Projects;
using ShipRight.Modules.Resources.Models;

namespace ShipRight.Modules.Transfer;

/// <summary>
/// Recreates a bundled project beside whatever already exists. Referenced resources get fresh ids so an
/// import never overwrites them; the source server and saved key files keep their ids and are reused when
/// already present, so shared servers/keys are not duplicated. The whole bundle is parsed before the first
/// write, so a bad bundle changes nothing.
/// </summary>
public class ProjectBundleImporter(TransferStores stores, string? targetRoot = null, string? passphrase = null)
{
    public async Task<ImportOutcome> ImportAsync(byte[] bytes)
    {
        var bundle = ProjectBundleReader.Read(BundleCrypto.Decrypt(bytes, passphrase));
        var now = DateTime.UtcNow;

        var name = await UniqueNameAsync(bundle.Project.Name);
        // Cloud stores index project ids as Guids, so imports must not reuse the source's slug-style id.
        var projectId = Guid.NewGuid().ToString();

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
        {
            var relocated = BundlePathRelocator.Resolve(r, targetRoot);
            await stores.Scripts.SaveAsync(relocated with { Id = scripts[r.Id], ProjectId = Scope(r.ProjectId), CreatedAt = now, ModifiedAt = now });
        }
        foreach (var r in bundle.Pipelines)
        {
            var relocated = BundlePathRelocator.Resolve(r, targetRoot);
            await stores.Pipelines.SaveAsync(relocated with
            {
                Id = pipelines[r.Id], ProjectId = Scope(r.ProjectId), CreatedAt = now, ModifiedAt = now,
                Steps = relocated.Steps.Select(s => s with { Id = Guid.NewGuid(), ScriptResourceId = Map(scripts, s.ScriptResourceId) }).ToList(),
            });
        }

        // Saved .pem files keep their ids so importing several projects that share a key does not duplicate it.
        foreach (var k in bundle.KeyFiles)
        {
            if (await stores.KeyFiles.GetByIdAsync(k.Id) is null)
                await stores.KeyFiles.SaveAsync(k with { CreatedAt = now, ModifiedAt = now });
        }

        var serverId = string.Empty;
        ServerConfig? server = null;
        if (bundle.Server is not null)
        {
            var source = bundle.Server;
            var sourceId = string.IsNullOrWhiteSpace(source.Id) ? $"srv-{Guid.NewGuid():N}"[..12] : source.Id;
            var existing = await stores.Servers.GetByIdAsync(sourceId);

            if (existing is not null && string.Equals(existing.Host, source.Host, StringComparison.OrdinalIgnoreCase))
            {
                // Same server already here: link to it and leave its settings untouched.
                serverId = existing.Id;
                server = existing;
            }
            else
            {
                serverId = existing is null ? sourceId : $"srv-{Guid.NewGuid():N}"[..12];
                server = source with
                {
                    Id = serverId,
                    SshKeyPath = string.Empty,
                    RebuildScriptResourceId = Map(scripts, source.RebuildScriptResourceId),
                    PipelineResourceId = Map(pipelines, source.PipelineResourceId),
                    ManagedSshKeyAuthorized = false,
                };
                if (bundle.ManagedKey is not null)
                {
                    await stores.SshKeys.SaveKeyPairAsync(projectId, bundle.ManagedKey.PrivateKey, bundle.ManagedKey.PublicKey);
                    server = server with
                    {
                        SshKeyPath = stores.SshKeys.GetPrivateKeyPath(projectId),
                        ManagedSshKey = true,
                        ManagedSshKeyAuthorized = source.ManagedSshKeyAuthorized,
                    };
                }
                await stores.Servers.SaveAsync(server);
            }
        }

        var portableProject = BundlePathRelocator.Resolve(bundle.Project, targetRoot);
        var project = portableProject with
        {
            Id = projectId,
            Name = name,
            ServerId = serverId,
            Server = server ?? portableProject.Server,
            CreatedAt = now,
            ModifiedAt = now,
            Services = portableProject.Services
                .Select(s => s with { DockerRegistryResourceId = Map(registries, s.DockerRegistryResourceId) })
                .ToList(),
            GitRepos = portableProject.GitRepos
                .Select(g => g with { CredentialResourceId = Map(credentials, g.CredentialResourceId) })
                .ToList(),
        };
        await stores.Projects.SaveAsync(project);
        if (bundle.Jit is not null)
            await stores.Jit.SaveAsync(projectId, bundle.Jit with
            {
                CredentialResourceId = Map(credentials, bundle.Jit.CredentialResourceId),
            });

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
