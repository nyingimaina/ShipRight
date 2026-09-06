namespace ShipRight.Modules.Resources;

public enum AwsCredentialsSourceKind
{
    None,
    Wsl,
    Native,
}

public sealed record AwsCredentialProfile(
    string Name,
    string? Region,
    bool HasKeys,
    string? UnsupportedReason);

public sealed record AwsCredentialsSource(
    AwsCredentialsSourceKind Kind,
    bool FileExists,
    IReadOnlyList<AwsCredentialProfile> Profiles);