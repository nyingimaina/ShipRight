namespace ShipRight.Modules.Resources;

/// <summary>POST body for /api/resources/aws-profiles/install-cli.</summary>
public sealed class AwsCliInstallRequest
{
    /// <summary>"auto" (default) or "pip" (user-level, no sudo).</summary>
    public string? Strategy { get; set; }
}