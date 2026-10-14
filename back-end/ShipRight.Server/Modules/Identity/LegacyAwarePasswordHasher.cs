using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;

namespace ShipRight.Modules.Identity;

// Accepts both Identity v3 hashes and the hashes ShipRight wrote before the switch:
// base64(16-byte salt + 20-byte PBKDF2-SHA256, 100,000 rounds). A correct old password is reported as
// SuccessRehashNeeded so Identity silently re-saves it in the new format on that sign-in (R1).
public sealed class LegacyAwarePasswordHasher : IPasswordHasher<AppUser>
{
    private const int LegacySaltSize = 16;
    private const int LegacyHashSize = 20;
    private const int LegacyIterations = 100_000;

    private readonly PasswordHasher<AppUser> _identity = new();

    public string HashPassword(AppUser user, string password) => _identity.HashPassword(user, password);

    public PasswordVerificationResult VerifyHashedPassword(AppUser user, string hashedPassword, string providedPassword)
    {
        if (string.IsNullOrEmpty(hashedPassword)) return PasswordVerificationResult.Failed;
        if (IsLegacy(hashedPassword, out var bytes)) return VerifyLegacy(bytes, providedPassword);

        // A damaged value is a failed sign-in, not an error page.
        try { return _identity.VerifyHashedPassword(user, hashedPassword, providedPassword); }
        catch (FormatException) { return PasswordVerificationResult.Failed; }
    }

    // Identity v3 starts with 0x01 and is 61 bytes; the old format is exactly 36 bytes.
    internal static bool IsLegacy(string stored, out byte[] bytes)
    {
        bytes = [];
        try { bytes = Convert.FromBase64String(stored); }
        catch (FormatException) { return false; }
        return bytes.Length == LegacySaltSize + LegacyHashSize;
    }

    private static PasswordVerificationResult VerifyLegacy(byte[] stored, string provided)
    {
        var salt = stored.AsSpan(0, LegacySaltSize);
        var expected = stored.AsSpan(LegacySaltSize, LegacyHashSize);
        var actual = Rfc2898DeriveBytes.Pbkdf2(provided, salt, LegacyIterations, HashAlgorithmName.SHA256, LegacyHashSize);
        return CryptographicOperations.FixedTimeEquals(expected, actual)
            ? PasswordVerificationResult.SuccessRehashNeeded
            : PasswordVerificationResult.Failed;
    }
}
