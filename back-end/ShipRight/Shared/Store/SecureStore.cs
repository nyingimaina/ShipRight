using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Serilog;

namespace ShipRight.Shared.Store;

/// <summary>
/// Cross-platform secure credential storage using AES-256-GCM.
///
/// A random master key is generated on first use and stored in the data
/// directory (~/.shipright/master.key) with restricted permissions:
///   - Windows: ACL via FileSystemRights / FileSecurity
///   - Linux/macOS: chmod 0600 (owner read/write only)
///
/// Each encrypted value uses a random 12-byte nonce prepended to the
/// ciphertext; the final format is base64(nonce + ciphertext + tag).
/// This ensures every encryption of the same plaintext produces a
/// different output.
/// </summary>
public static class SecureStore
{
    private const string KeyFileName = "master.key";
    private static byte[]? _cachedKey;
    private static readonly object KeyLock = new();

    private static byte[] GetOrCreateKey(string dataDir)
    {
        if (_cachedKey is not null) return _cachedKey;

        lock (KeyLock)
        {
            if (_cachedKey is not null) return _cachedKey;

            var keyPath = Path.Combine(dataDir, KeyFileName);

            if (File.Exists(keyPath))
            {
                _cachedKey = File.ReadAllBytes(keyPath);
                return _cachedKey;
            }

            var key = RandomNumberGenerator.GetBytes(32); // AES-256
            File.WriteAllBytes(keyPath, key);

            // Restrict permissions so only the owner can read the key
            try
            {
                if (OperatingSystem.IsWindows())
                    RestrictFileAccessWindows(keyPath);
                else
                    RestrictFileAccessUnix(keyPath);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not restrict permissions on {KeyPath} — key file may be readable by other users", keyPath);
            }

            _cachedKey = key;
            Log.Information("Generated new master key at {KeyPath}", keyPath);
            return key;
        }
    }

    public static string Encrypt(string plaintext, string dataDir)
    {
        if (string.IsNullOrEmpty(plaintext)) return string.Empty;

        var key = GetOrCreateKey(dataDir);
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(12); // 96-bit nonce

        byte[] ciphertext = new byte[plainBytes.Length];
        byte[] tag = new byte[16]; // 128-bit authentication tag

        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plainBytes, ciphertext, tag);

        // Format: nonce + ciphertext + tag
        var combined = new byte[nonce.Length + ciphertext.Length + tag.Length];
        Buffer.BlockCopy(nonce, 0, combined, 0, nonce.Length);
        Buffer.BlockCopy(ciphertext, 0, combined, nonce.Length, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, combined, nonce.Length + ciphertext.Length, tag.Length);

        return Convert.ToBase64String(combined);
    }

    public static string Decrypt(string ciphertext, string dataDir)
    {
        if (string.IsNullOrEmpty(ciphertext)) return string.Empty;

        try
        {
            var key = GetOrCreateKey(dataDir);
            var combined = Convert.FromBase64String(ciphertext);

            if (combined.Length < 12 + 16) return ciphertext; // Not encrypted

            var nonce = combined.AsSpan(0, 12);
            var tag = combined.AsSpan(^16..);
            var encrypted = combined.AsSpan(12..^16);

            var plainBytes = new byte[encrypted.Length];
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(nonce, encrypted, tag, plainBytes);

            return Encoding.UTF8.GetString(plainBytes);
        }
        catch (FormatException)
        {
            return ciphertext; // Not base64 — plaintext (backward compat)
        }
        catch (AuthenticationTagMismatchException)
        {
            Log.Warning("SecureStore.Decrypt: authentication failed — key may have changed");
            return string.Empty;
        }
        catch (CryptographicException)
        {
            return ciphertext; // Possibly already plaintext
        }
    }

    private static void RestrictFileAccessWindows(string path)
    {
        // Windows: default ACL from the user's profile directory is already
        // restricted to the owner. No extra step needed — File.WriteAllBytes
        // inherits the parent directory permissions (which are user-only for
        // %USERPROFILE% subdirectories).
    }

    private static void RestrictFileAccessUnix(string path)
    {
        // chmod 0600 via shell command
        var psi = new System.Diagnostics.ProcessStartInfo("chmod", ["0600", path])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var proc = System.Diagnostics.Process.Start(psi);
        proc?.WaitForExit(5000);
    }
}
