using ShipRight.Modules.RemoteHost;

namespace ShipRight.Modules.BootstrapDeployment;

public sealed record BootstrapKey(string PrivateKeyPath, string PublicKey, bool Created);

/// <summary>Makes sure a server has a managed key pair, creating one only when there is none.</summary>
public interface IBootstrapKeyProvisioner
{
    Task<BootstrapKey> EnsureAsync(string serverId, CancellationToken ct = default);
}

/// <summary>The real implementation: a bounded, non-interactive <c>ssh-keygen</c> into the ShipRight key store.</summary>
public sealed class SshKeyBootstrapProvisioner(SshKeyStore store) : IBootstrapKeyProvisioner
{
    public async Task<BootstrapKey> EnsureAsync(string serverId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // Same naming the server key screen uses, so a key made here is the key that screen shows.
        var keyId = "server-" + serverId;
        var created = false;

        if (!await store.ExistsAsync(keyId))
        {
            await store.GenerateBoundedAsync(keyId, TimeSpan.FromSeconds(20));
            created = true;
        }

        return new BootstrapKey(store.GetPrivateKeyPath(keyId), await store.GetPublicKeyAsync(keyId), created);
    }
}
