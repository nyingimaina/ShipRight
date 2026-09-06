namespace ShipRight.Modules.Resources.Models;

public enum RegistryAuthType
{
    Password,
    AwsEcr,
}

public record DockerRegistryResource
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = string.Empty;
    public string Registry { get; init; } = string.Empty;
    public string Username { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
    public RegistryAuthType AuthType { get; init; } = RegistryAuthType.Password;
    public string AwsRegion { get; init; } = string.Empty;
    public Guid? AwsProfileResourceId { get; init; }
    public List<string> Tags { get; init; } = [];
    public DateTime CreatedAt { get; init; }
    public DateTime ModifiedAt { get; set; }
}
