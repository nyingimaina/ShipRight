using ShipRight.Modules.JitDeploy;
using ShipRight.Modules.Projects;

namespace ShipRight.Modules.BootstrapDeployment;

/// <summary>Which from-scratch template, if any, fills in the blanks.</summary>
public enum BootstrapPreset
{
    None,
    ShipRightSelf,
}

/// <summary>One service to build on the server, named as it appears in the compose file.</summary>
public sealed record BootstrapService
{
    public string Name { get; init; } = string.Empty;
    public string Image { get; init; } = string.Empty;
    public string Dockerfile { get; init; } = string.Empty;
    public string Context { get; init; } = ".";
    public string? Target { get; init; }
    public Dictionary<string, string>? Args { get; init; }
}

/// <summary>
/// Everything a person or a desktop flow must decide to create a deployment from scratch. Everything else — the
/// server record, the project, the build-on-server settings and the environment — is written by ShipRight.
/// </summary>
public sealed record BootstrapRequest
{
    public BootstrapPreset Preset { get; init; } = BootstrapPreset.None;

    // The app and how it is built.
    public string ProjectName { get; init; } = string.Empty;
    public string RepoUrl { get; init; } = string.Empty;
    public string Branch { get; init; } = "master";
    public string ComposeFile { get; init; } = "docker-compose.yml";
    public string ComposeProject { get; init; } = string.Empty;
    public List<BootstrapService> Services { get; init; } = [];
    public Dictionary<string, string> Env { get; init; } = new(StringComparer.Ordinal);

    // The server that will run it.
    public string ServerId { get; init; } = string.Empty;
    public string ServerName { get; init; } = string.Empty;
    public string Host { get; init; } = string.Empty;
    public string Username { get; init; } = string.Empty;
    public string SshKeyPath { get; init; } = string.Empty;
    public string RemoteWorkingDir { get; init; } = string.Empty;
    public string Domain { get; init; } = string.Empty;
    public string HealthCheckUrl { get; init; } = string.Empty;

    // Reuse an existing project id when updating a bootstrap that was written before.
    public string ProjectId { get; init; } = string.Empty;
    public bool UseDeployKey { get; init; } = true;
    public Guid? CredentialResourceId { get; init; }
}

/// <summary>One human-readable step in a bootstrap, in the order it should happen.</summary>
public sealed record BootstrapStep(string Id, string Title, string Detail);

/// <summary>
/// A complete configuration written from scratch: the server, the project and the build-on-server settings. It is
/// produced without touching anything, so it can be shown to a person before a single file is written.
/// </summary>
public sealed record BootstrapPlan
{
    public ServerConfig Server { get; init; } = new();
    public ProjectConfig Project { get; init; } = new();
    public JitDeployConfig Jit { get; init; } = new();
    public IReadOnlyList<string> Problems { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public IReadOnlyList<string> DeployBlockers { get; init; } = [];
    public IReadOnlyList<BootstrapStep> Steps { get; init; } = [];
    public bool Ok => Problems.Count == 0;
    public bool DeployReady => DeployBlockers.Count == 0;
}

/// <summary>What the applier wrote, and what the person should do next.</summary>
public sealed record BootstrapApplyResult
{
    public bool Ok { get; init; }
    public string? ProjectId { get; init; }
    public string? ServerId { get; init; }
    /// <summary>The public key to add to the server, when ShipRight made a managed key.</summary>
    public string? PublicKey { get; init; }
    public IReadOnlyList<string> Problems { get; init; } = [];
    public IReadOnlyList<string> NextSteps { get; init; } = [];
}
