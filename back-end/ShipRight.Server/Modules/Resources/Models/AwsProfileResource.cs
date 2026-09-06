namespace ShipRight.Modules.Resources.Models;

/// <summary>
/// A saved AWS authentication profile used when acquiring ECR login tokens.
/// Either references a named profile from the machine's ~/.aws config (ProfileName)
/// or carries explicit access keys (AccessKeyId + SecretAccessKey, optionally a
/// session token).
/// </summary>
public record AwsProfileResource
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = string.Empty;
    public string ProfileName { get; init; } = string.Empty;
    public string AccessKeyId { get; init; } = string.Empty;
    public string SecretAccessKey { get; init; } = string.Empty;
    public string SessionToken { get; init; } = string.Empty;
    public string DefaultRegion { get; init; } = string.Empty;
    public List<string> Tags { get; init; } = [];
    public DateTime CreatedAt { get; init; }
    public DateTime ModifiedAt { get; set; }

    public bool UsesExplicitKeys =>
        !string.IsNullOrEmpty(AccessKeyId) && !string.IsNullOrEmpty(SecretAccessKey);
}
