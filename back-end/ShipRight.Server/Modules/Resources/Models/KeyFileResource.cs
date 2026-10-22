namespace ShipRight.Modules.Resources.Models;

/// <summary>
/// A company-shared .pem private-key file that can be reused when authorizing a server (R5).
/// The <see cref="Content"/> is a secret: it is written once, never returned by any endpoint and
/// never logged (R10); metadata (name, tags, dates) is all a client ever sees.
/// </summary>
public record KeyFileResource
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = string.Empty;
    /// <summary>The .pem private-key content. Write-only from the app's point of view.</summary>
    public string Content { get; init; } = string.Empty;
    public List<string> Tags { get; init; } = [];
    public DateTime CreatedAt { get; init; }
    public DateTime ModifiedAt { get; set; }
}
