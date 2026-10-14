namespace ShipRight.Modules.RemoteHost;

public interface IRemoteHostProvider
{
    string ProviderType { get; }

    /// One-time password bootstrap: connects with password and appends the public key to ~/.ssh/authorized_keys.
    Task AuthorizeKeyAsync(RemoteHostConfig config, string password, string publicKey, CancellationToken ct = default);

    /// One-time bootstrap with a saved .pem instead of a password (cloud servers usually have none):
    /// connects with the supplied private key and appends the managed public key to ~/.ssh/authorized_keys.
    Task AuthorizeKeyWithFileAsync(RemoteHostConfig config, string privateKeyContent, string publicKey, CancellationToken ct = default);

    /// Returns true when the managed key can authenticate successfully.
    Task<bool> IsKeyAuthorizedAsync(RemoteHostConfig config, string keyPath, CancellationToken ct = default);
}
