namespace ShipRight.Modules.Resources.Models;

public record DockerRegistryResource
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = string.Empty;
    public string Registry { get; init; } = string.Empty;
    public string Username { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
    public DateTime ModifiedAt { get; set; }
}
