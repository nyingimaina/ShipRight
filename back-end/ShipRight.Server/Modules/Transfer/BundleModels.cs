using ShipRight.Modules.Projects;
using ShipRight.Modules.Resources.Models;
using ShipRight.Modules.Resources.Stores;
using ShipRight.Modules.Servers;

namespace ShipRight.Modules.Transfer;

/// <summary>Every store a project export/import touches, grouped so the transfer classes take one argument.</summary>
public record TransferStores(
    IProjectStore Projects,
    IServerStore Servers,
    IDockerRegistryResourceStore Registries,
    IAwsProfileResourceStore AwsProfiles,
    ICredentialResourceStore Credentials,
    IScriptResourceStore Scripts,
    IPipelineResourceStore Pipelines);

/// <summary>
/// Something the person moving the project must look at. Codes: secret-omitted, path, remote-path,
/// ssh-key-not-transferred, aws-profile-name, dangling-reference (export) and name-clash,
/// version-differs (inspect).
/// </summary>
public record BundleWarning
{
    public string Severity { get; init; } = "warning";
    public string Code { get; init; } = string.Empty;
    public string Location { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}

public record BundleManifest
{
    /// <summary>Bump when the bundle layout changes in a way an older ShipRight cannot read.</summary>
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; init; } = CurrentFormatVersion;
    public string ShipRightVersion { get; init; } = string.Empty;
    public DateTime ExportedAt { get; init; }
    public string ProjectId { get; init; } = string.Empty;
    public string ProjectName { get; init; } = string.Empty;
    /// <summary>Every entry in the zip except this manifest; import refuses a bundle missing any of them.</summary>
    public List<string> Files { get; init; } = [];
    public List<BundleWarning> Warnings { get; init; } = [];
}

public record BundleInspection
{
    public string ProjectName { get; init; } = string.Empty;
    public BundleManifest Manifest { get; init; } = new();
    public bool Importable { get; init; }
    public List<string> Errors { get; init; } = [];
    public List<BundleWarning> Warnings { get; init; } = [];
    public Dictionary<string, int> Counts { get; init; } = [];
}

public record ImportOutcome(string ProjectId, string ProjectName, List<BundleWarning> Warnings);

/// <summary>The bundle is unreadable, incomplete or from a format this ShipRight cannot import.</summary>
public class BundleException(string message) : Exception(message);

/// <summary>A fully parsed bundle held in memory — nothing is written anywhere until the importer decides to.</summary>
public record ProjectBundle
{
    public BundleManifest Manifest { get; init; } = new();
    public ProjectConfig Project { get; init; } = new();
    public ServerConfig? Server { get; init; }
    public List<DockerRegistryResource> Registries { get; init; } = [];
    public List<AwsProfileResource> AwsProfiles { get; init; } = [];
    public List<CredentialResource> Credentials { get; init; } = [];
    public List<ScriptResource> Scripts { get; init; } = [];
    public List<PipelineResource> Pipelines { get; init; } = [];
}
