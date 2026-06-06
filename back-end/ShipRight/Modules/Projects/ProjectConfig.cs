namespace ShipRight.Modules.Projects;

public record DetectRequest(string RootPath);

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

public record ServerConfig
{
    public string Host { get; init; } = string.Empty;
    public string Username { get; init; } = string.Empty;
    public string SshKeyPath { get; init; } = string.Empty;
    public string RemoteWorkingDir { get; init; } = string.Empty;
    public string RebuildScript { get; init; } = "rebuild.sh";
}

public record ProjectConfig
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public List<ServiceConfig> Services { get; init; } = new();
    public List<GitConfig> GitRepos { get; init; } = new();
    public WslConfig Wsl { get; init; } = new();
    public ServerConfig Server { get; init; } = new();
    public DateTime CreatedAt { get; init; }
    public DateTime ModifiedAt { get; set; }
}
