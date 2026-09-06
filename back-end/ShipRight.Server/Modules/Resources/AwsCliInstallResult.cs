namespace ShipRight.Modules.Resources;

public sealed record AwsCliInstallResult(
    bool Success,
    string Message,
    bool RequiresManualInstall = false,
    string? Command = null,
    string? OutputTail = null);