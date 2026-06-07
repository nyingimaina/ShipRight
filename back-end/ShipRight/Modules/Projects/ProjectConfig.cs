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
    public List<ServiceConfig> Services { get; init; } = new();
    public List<GitConfig> GitRepos { get; init; } = new();
    public WslConfig Wsl { get; init; } = new();
    public ServerConfig Server { get; init; } = new();
    public DatabaseConfig? Database { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime ModifiedAt { get; set; }
}
