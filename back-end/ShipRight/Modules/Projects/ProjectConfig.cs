namespace ShipRight.Modules.Projects;

public record DetectRequest(string RootPath);

public enum DbProviderType { MariaDb, SqlServer }

public record DatabaseConfig
{
    public DbProviderType Provider    { get; init; } = DbProviderType.MariaDb;
    public string ContainerName       { get; init; } = string.Empty;
    public string DatabaseName        { get; init; } = string.Empty;
    public string RootUser            { get; init; } = "root";
    public int    BackupRetainCount   { get; init; } = 10;
}

public record ServiceConfig
{
    public string Name { get; init; } = string.Empty;
    public string VersionFilePath { get; init; } = string.Empty;
    public string BuildContextPath { get; init; } = string.Empty;
    public string DockerImageName { get; init; } = string.Empty;
    /// <summary>
    /// Docker registry host (e.g. "ghcr.io", "myregistry.azurecr.io").
    /// When empty, the registry is inferred from DockerImageName. When the
    /// image name has no explicit registry (e.g. "nyingi/app"), docker.io is used.
    /// </summary>
    public string DockerRegistry { get; init; } = string.Empty;
    /// <summary>
    /// The service key in docker-compose.yml (e.g. "api", "web").
    /// When set on all built services, deploy uses targeted --no-deps restart
    /// instead of bringing the whole stack down and up.
    /// </summary>
    public string ComposeServiceName { get; init; } = string.Empty;
    /// <summary>
    /// Pre-configured Docker registry username. When set alongside DockerPassword,
    /// the build pipeline uses these credentials directly instead of prompting.
    /// </summary>
    public string DockerUsername { get; init; } = string.Empty;
    /// <summary>
    /// Pre-configured Docker registry password/token. Stored in projects.json
    /// alongside other config. Leave empty to enter credentials at build time.
    /// </summary>
    public string DockerPassword { get; init; } = string.Empty;
}

public record GitConfig
{
    public string RepoPath { get; init; } = string.Empty;
    public string DeployBranch { get; init; } = "master";
}

public record WslConfig
{
    public string WorkingDir { get; init; } = string.Empty;
}

/// <summary>
/// GitScript  — ShipRight syncs docker-compose.yml via git; server runs git pull + your script.
/// GitCompose — ShipRight syncs docker-compose.yml via git; server runs git pull + docker compose up.
/// EnvCompose — ShipRight injects image tags as env vars at deploy time; no compose-repo git round-trip.
/// </summary>
public enum DeployMode { GitScript, GitCompose, EnvCompose }

public record ServerConfig
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Host { get; init; } = string.Empty;
    public string Username { get; init; } = string.Empty;
    public string SshKeyPath { get; init; } = string.Empty;
    public string RemoteWorkingDir { get; init; } = string.Empty;
    public string RebuildScript { get; init; } = "rebuild.sh";
    public DeployMode DeployMode { get; init; } = DeployMode.GitScript;
}

public record ProjectConfig
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public int Version { get; init; } = 2;
    public string ServerId { get; init; } = string.Empty;
    public List<ServiceConfig> Services { get; init; } = new();
    public List<GitConfig> GitRepos { get; init; } = new();
    public WslConfig Wsl { get; init; } = new();
    public ServerConfig Server { get; init; } = new();
    public DatabaseConfig? Database { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime ModifiedAt { get; set; }
}
