using System.IO.Compression;
using System.Text;
using Newtonsoft.Json;
using ShipRight.Modules.Projects;
using ShipRight.Modules.Resources.Models;

namespace ShipRight.Modules.Transfer;

/// <summary>
/// Packs one project and everything it relies on — its server, registries, AWS profiles, git credentials,
/// scripts and pipelines — into a zip with one JSON file per record. Secrets are never written; the
/// manifest instead lists each one omitted and every machine-specific path, so the person importing
/// knows what to re-enter or fix.
/// </summary>
public class ProjectBundleExporter(TransferStores stores, string shipRightVersion)
{
    public async Task<byte[]?> ExportAsync(string projectId)
    {
        var project = await stores.Projects.GetByIdAsync(projectId);
        if (project is null) return null;

        var warnings = new List<BundleWarning>();
        var files = new Dictionary<string, object>();

        var server = await LoadServerAsync(project);
        var scripts = new Dictionary<Guid, ScriptResource>();
        var pipelines = new Dictionary<Guid, PipelineResource>();
        var credentials = new Dictionary<Guid, CredentialResource>();
        var registries = new Dictionary<Guid, DockerRegistryResource>();
        var awsProfiles = new Dictionary<Guid, AwsProfileResource>();

        async Task AddScriptAsync(Guid? id, string from)
        {
            if (id is null || scripts.ContainsKey(id.Value)) return;
            var s = await stores.Scripts.GetByIdAsync(id.Value);
            if (s is null) warnings.Add(Dangling("script", id.Value, from));
            else scripts[s.Id] = s;
        }
        async Task AddPipelineAsync(Guid? id, string from)
        {
            if (id is null || pipelines.ContainsKey(id.Value)) return;
            var p = await stores.Pipelines.GetByIdAsync(id.Value);
            if (p is null) { warnings.Add(Dangling("pipeline", id.Value, from)); return; }
            pipelines[p.Id] = p;
            foreach (var step in p.Steps) await AddScriptAsync(step.ScriptResourceId, $"pipeline '{p.Name}'");
        }

        foreach (var svc in project.Services.Where(s => s.DockerRegistryResourceId is not null))
        {
            var id = svc.DockerRegistryResourceId!.Value;
            if (registries.ContainsKey(id)) continue;
            var reg = await stores.Registries.GetByIdAsync(id);
            if (reg is null) { warnings.Add(Dangling("registry", id, $"service '{svc.Name}'")); continue; }
            registries[id] = reg;
            if (reg.AwsProfileResourceId is { } awsId && !awsProfiles.ContainsKey(awsId))
            {
                var aws = await stores.AwsProfiles.GetByIdAsync(awsId);
                if (aws is null) warnings.Add(Dangling("AWS profile", awsId, $"registry '{reg.Name}'"));
                else awsProfiles[awsId] = aws;
            }
        }
        foreach (var repo in project.GitRepos.Where(g => g.CredentialResourceId is not null))
        {
            var id = repo.CredentialResourceId!.Value;
            if (credentials.ContainsKey(id)) continue;
            var cred = await stores.Credentials.GetByIdAsync(id);
            if (cred is null) warnings.Add(Dangling("credential", id, $"git repo '{repo.RepoPath}'"));
            else credentials[id] = cred;
        }
        if (server is not null)
        {
            await AddScriptAsync(server.RebuildScriptResourceId, $"server '{server.Name}'");
            await AddPipelineAsync(server.PipelineResourceId, $"server '{server.Name}'");
        }
        foreach (var c in await stores.Credentials.GetByProjectAsync(project.Id)) credentials[c.Id] = c;
        foreach (var p in await stores.Pipelines.GetByProjectAsync(project.Id)) await AddPipelineAsync(p.Id, "project");
        foreach (var s in await stores.Scripts.GetByProjectAsync(project.Id)) scripts[s.Id] = s;

        warnings.AddRange(BundleWarnings.ForProject(project));
        if (server is not null) warnings.AddRange(BundleWarnings.ForServer(server));
        foreach (var r in registries.Values) warnings.AddRange(BundleWarnings.ForRegistry(r));
        foreach (var a in awsProfiles.Values) warnings.AddRange(BundleWarnings.ForAwsProfile(a));
        foreach (var c in credentials.Values) warnings.AddRange(BundleWarnings.ForCredential(c));
        foreach (var s in scripts.Values) warnings.AddRange(BundleWarnings.ForScript(s));
        foreach (var p in pipelines.Values) warnings.AddRange(BundleWarnings.ForPipeline(p));

        files["project.json"] = BundleSecrets.Strip(project);
        if (server is not null) files["server.json"] = BundleSecrets.Strip(server);
        foreach (var r in registries.Values) files[$"resources/registries/{r.Id}.json"] = BundleSecrets.Strip(r);
        foreach (var a in awsProfiles.Values) files[$"resources/aws-profiles/{a.Id}.json"] = BundleSecrets.Strip(a);
        foreach (var c in credentials.Values) files[$"resources/credentials/{c.Id}.json"] = BundleSecrets.Strip(c);
        foreach (var s in scripts.Values) files[$"resources/scripts/{s.Id}.json"] = s;
        foreach (var p in pipelines.Values) files[$"resources/pipelines/{p.Id}.json"] = p;

        var manifest = new BundleManifest
        {
            ShipRightVersion = shipRightVersion,
            ExportedAt = DateTime.UtcNow,
            ProjectId = project.Id,
            ProjectName = project.Name,
            Files = files.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList(),
            Warnings = warnings,
        };

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(zip, "manifest.json", manifest);
            foreach (var name in manifest.Files) Write(zip, name, files[name]);
        }
        return ms.ToArray();
    }

    // The project's server lives in the server store; fall back to the copy embedded in the project.
    private async Task<ServerConfig?> LoadServerAsync(ProjectConfig project)
    {
        if (!string.IsNullOrEmpty(project.ServerId))
        {
            var stored = await stores.Servers.GetByIdAsync(project.ServerId);
            if (stored is not null) return stored;
        }
        return string.IsNullOrWhiteSpace(project.Server.Host) ? null : project.Server;
    }

    private static BundleWarning Dangling(string kind, Guid id, string from) => new()
    {
        Code = "dangling-reference", Location = from,
        Message = $"{from} points to a {kind} ({id}) that no longer exists, so it was not exported.",
    };

    private static void Write(ZipArchive zip, string name, object value)
    {
        using var w = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
        w.Write(JsonConvert.SerializeObject(value, Formatting.Indented));
    }
}
