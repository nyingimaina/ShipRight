using System.Security.Cryptography;
using System.Text;

namespace ShipRight.Modules.Transfer;

/// <summary>
/// Optional passphrase protection for secret-bearing bundles. The plain zip is sealed in an
/// AES-GCM envelope, so a manifest, a secret or a key file is never readable without the passphrase.
/// </summary>
public static class BundleCrypto
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("SHRSEC01");
    private const int SaltBytes = 16;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const int KeyBytes = 32;
    private const int Iterations = 100_000;

    public static bool IsEncrypted(byte[] bytes) =>
        bytes.Length >= Magic.Length && bytes.AsSpan(0, Magic.Length).SequenceEqual(Magic);

    public static byte[] Encrypt(byte[] plain, string passphrase)
    {
        ArgumentException.ThrowIfNullOrEmpty(passphrase);

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var key = DeriveKey(passphrase, salt);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagBytes];

        using (var aes = new AesGcm(key, TagBytes))
            aes.Encrypt(nonce, plain, cipher, tag);

        var envelope = new byte[Magic.Length + SaltBytes + NonceBytes + TagBytes + cipher.Length];
        var offset = 0;
        void Copy(byte[] source)
        {
            source.CopyTo(envelope, offset);
            offset += source.Length;
        }
        Copy(Magic); Copy(salt); Copy(nonce); Copy(tag); Copy(cipher);
        CryptographicOperations.ZeroMemory(key);
        return envelope;
    }

    public static byte[] Decrypt(byte[] bytes, string? passphrase)
    {
        if (!IsEncrypted(bytes))
        {
            if (!string.IsNullOrEmpty(passphrase))
                throw new BundleException("This export is not encrypted. Remove the passphrase and try again.");
            return bytes;
        }
        if (string.IsNullOrEmpty(passphrase))
            throw new BundleException("This export is encrypted. Enter its passphrase to inspect or import it.");

        var offset = Magic.Length;
        var salt = bytes.AsSpan(offset, SaltBytes).ToArray(); offset += SaltBytes;
        var nonce = bytes.AsSpan(offset, NonceBytes).ToArray(); offset += NonceBytes;
        var tag = bytes.AsSpan(offset, TagBytes).ToArray(); offset += TagBytes;
        var cipher = bytes.AsSpan(offset).ToArray();
        var key = DeriveKey(passphrase, salt);
        var plain = new byte[cipher.Length];

        try
        {
            using var aes = new AesGcm(key, TagBytes);
            aes.Decrypt(nonce, cipher, tag, plain);
        }
        catch (CryptographicException)
        {
            throw new BundleException("Incorrect passphrase, or this export has been damaged.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
        return plain;
    }

    private static byte[] DeriveKey(string passphrase, byte[] salt)
    {
        var key = new byte[KeyBytes];
        Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, key, Iterations, HashAlgorithmName.SHA256);
        return key;
    }
}
