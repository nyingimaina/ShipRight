using Microsoft.AspNetCore.Identity;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Identity;
using ShipRight.Tests.Modules.Auth;

namespace ShipRight.Tests.Modules.Identity;

[TestClass]
public class LegacyAwarePasswordHasherTests
{
    private readonly LegacyAwarePasswordHasher _hasher = new();
    private readonly AppUser _user = new();

    [TestMethod]
    public void LegacyHash_WithCorrectPassword_SucceedsAndAsksForRehash()
    {
        // R1, R12
        var stored = AuthCharacterizationTests.BuildLegacyHash("correct horse");

        Assert.AreEqual(PasswordVerificationResult.SuccessRehashNeeded, _hasher.VerifyHashedPassword(_user, stored, "correct horse"));
    }

    [TestMethod]
    public void LegacyHash_WithWrongPassword_Fails()
    {
        // R1
        var stored = AuthCharacterizationTests.BuildLegacyHash("correct horse");

        Assert.AreEqual(PasswordVerificationResult.Failed, _hasher.VerifyHashedPassword(_user, stored, "wrong horse"));
    }

    [TestMethod]
    public void NewHash_RoundTrips_AndIsNotTakenForLegacy()
    {
        // R1
        var hash = _hasher.HashPassword(_user, "correct horse");

        Assert.IsFalse(LegacyAwarePasswordHasher.IsLegacy(hash, out _));
        Assert.AreEqual(PasswordVerificationResult.Success, _hasher.VerifyHashedPassword(_user, hash, "correct horse"));
        Assert.AreEqual(PasswordVerificationResult.Failed, _hasher.VerifyHashedPassword(_user, hash, "wrong horse"));
        Assert.IsFalse(hash.Contains("correct horse"));
    }

    [TestMethod]
    public void EmptyOrGarbageHash_Fails_WithoutThrowing()
    {
        // R1: a damaged hash is a failed sign-in, never an error page.
        Assert.AreEqual(PasswordVerificationResult.Failed, _hasher.VerifyHashedPassword(_user, "", "x"));
        Assert.AreEqual(PasswordVerificationResult.Failed, _hasher.VerifyHashedPassword(_user, "not base64 !!", "x"));
    }
}
