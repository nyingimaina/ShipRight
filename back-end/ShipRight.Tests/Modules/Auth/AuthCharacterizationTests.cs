using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Auth.Services;
using ShipRight.Server;

namespace ShipRight.Tests.Modules.Auth;

// Written against the old sign-in before the Identity switch, then kept passing through it. They pin what must
// survive: the stored password-hash format (R1/R12), the mail-sender settings (R13) and start-up validation (R13).
[TestClass]
public class AuthCharacterizationTests
{
    // Built independently of the app: base64(16B salt + 20B PBKDF2-SHA256 at 100k iterations). This is exactly
    // what the removed PasswordHelper wrote, so it stands in for every account created before the switch.
    internal static string BuildLegacyHash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, 20);
        return Convert.ToBase64String(salt.Concat(hash).ToArray());
    }

    [TestMethod]
    public void LegacyHash_Is36BytesOfBase64()
    {
        // R1, R12
        Assert.AreEqual(36, Convert.FromBase64String(BuildLegacyHash("another-secret")).Length);
    }

    [TestMethod]
    public void MailSender_WithoutMailSettings_NamesTheMissingSetting()
    {
        // R13: the forgot-password mail settings stay, and a missing one is named.
        var saved = Environment.GetEnvironmentVariable("SHIPRIGHT__SMTP_HOST");
        Environment.SetEnvironmentVariable("SHIPRIGHT__SMTP_HOST", null);
        try
        {
            var ex = Assert.ThrowsException<InvalidOperationException>(() => new SmtpPasswordResetEmailSender());
            StringAssert.Contains(ex.Message, "SHIPRIGHT__SMTP_HOST");
        }
        finally
        {
            Environment.SetEnvironmentVariable("SHIPRIGHT__SMTP_HOST", saved);
        }
    }

    [TestMethod]
    public void ValidateCloudSettings_MissingConnection_NamesSettingNotValue()
    {
        // R13
        var ex = Assert.ThrowsException<InvalidOperationException>(() =>
            CloudConfiguration.ValidateCloudSettings(null, "https://a.test"));

        StringAssert.Contains(ex.Message, "SHIPRIGHT__DB_CONNECTION");
    }

    [TestMethod]
    public void ValidateCloudSettings_MissingCors_NamesSetting()
    {
        // R13
        var ex = Assert.ThrowsException<InvalidOperationException>(() =>
            CloudConfiguration.ValidateCloudSettings("Server=db;Password=s3", null));

        StringAssert.Contains(ex.Message, "SHIPRIGHT__CORS_ORIGINS");
    }

    [TestMethod]
    public void ValidateCloudSettings_PlaceholderPassword_IsRefusedWithoutEchoingIt()
    {
        // R13
        var ex = Assert.ThrowsException<InvalidOperationException>(() =>
            CloudConfiguration.ValidateCloudSettings("Server=db;Password=changeme", "https://a.test"));

        Assert.IsFalse(ex.Message.Contains("Server=db", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ValidateCloudSettings_RemovedSettingsLeftInEnvironment_AreIgnored()
    {
        // R13: an old JWT key or admin seed still set in someone's environment must not stop start-up.
        Environment.SetEnvironmentVariable("SHIPRIGHT__JWT_KEY", "left-over");
        Environment.SetEnvironmentVariable("SHIPRIGHT__ADMIN_EMAIL", "old@example.test");
        Environment.SetEnvironmentVariable("SHIPRIGHT__ADMIN_PASSWORD", "left-over");
        try
        {
            CloudConfiguration.ValidateCloudSettings("Server=db;Password=s3", "https://a.test");
        }
        finally
        {
            Environment.SetEnvironmentVariable("SHIPRIGHT__JWT_KEY", null);
            Environment.SetEnvironmentVariable("SHIPRIGHT__ADMIN_EMAIL", null);
            Environment.SetEnvironmentVariable("SHIPRIGHT__ADMIN_PASSWORD", null);
        }
    }

    [TestMethod]
    public void ValidateCloudSettings_GoogleOptional_ButNotHalfSet()
    {
        // R13, R3: Google is optional, but an id without a secret (or the reverse) names what is missing.
        CloudConfiguration.ValidateCloudSettings("Server=db;Password=s3", "https://a.test", null, null);

        var ex = Assert.ThrowsException<InvalidOperationException>(() =>
            CloudConfiguration.ValidateCloudSettings("Server=db;Password=s3", "https://a.test", "id-only", null));
        StringAssert.Contains(ex.Message, "SHIPRIGHT__GOOGLE_CLIENT_SECRET");
        Assert.IsFalse(ex.Message.Contains("id-only", StringComparison.Ordinal));
    }
}
