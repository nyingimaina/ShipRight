namespace ShipRight.Modules.RemoteHost;

public interface IRemoteHostProvider
{
    string ProviderType { get; }

    /// One-time password bootstrap: connects with password and appends the public key to ~/.ssh/authorized_keys.
    Task AuthorizeKeyAsync(RemoteHostConfig config, string password, string publicKey, CancellationToken ct = default);

    /// Returns true when the managed key can authenticate successfully.
    Task<bool> IsKeyAuthorizedAsync(RemoteHostConfig config, string keyPath, CancellationToken ct = default);
}
