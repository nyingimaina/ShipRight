namespace ShipRight.Modules.Resources.Models;

public record CredentialResource
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
    public string? HostPattern { get; init; }
    public string? ProjectId { get; init; }
    public List<string> Tags { get; init; } = [];
    public DateTime CreatedAt { get; init; }
    public DateTime ModifiedAt { get; set; }
}
