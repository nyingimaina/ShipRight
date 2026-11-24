using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Transfer;

namespace ShipRight.Tests.Modules.Transfer;

[TestClass]
public class BundleCryptoTests
{
    private static readonly byte[] Plain = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];

    [TestMethod]
    public void RoundTrip_RestoresTheExactBytes()
    {
        var encrypted = BundleCrypto.Encrypt(Plain, "a-strong-passphrase");

        Assert.IsTrue(BundleCrypto.IsEncrypted(encrypted));
        CollectionAssert.AreEqual(Plain, BundleCrypto.Decrypt(encrypted, "a-strong-passphrase"));
    }

    [TestMethod]
    public void Encrypt_UsesFreshSaltAndNonce()
    {
        CollectionAssert.AreNotEqual(
            BundleCrypto.Encrypt(Plain, "same-passphrase"),
            BundleCrypto.Encrypt(Plain, "same-passphrase"));
    }

    [TestMethod]
    public void Decrypt_WithTheWrongPassphrase_Throws()
    {
        var encrypted = BundleCrypto.Encrypt(Plain, "right-passphrase");

        Assert.ThrowsException<BundleException>(() => BundleCrypto.Decrypt(encrypted, "wrong-passphrase"));
    }

    [TestMethod]
    public void Decrypt_WithoutAPassphrase_AsksForOne()
    {
        var encrypted = BundleCrypto.Encrypt(Plain, "right-passphrase");

        var ex = Assert.ThrowsException<BundleException>(() => BundleCrypto.Decrypt(encrypted, null));
        StringAssert.Contains(ex.Message, "passphrase");
    }

    [TestMethod]
    public void Decrypt_ATamperedBundle_Throws()
    {
        var encrypted = BundleCrypto.Encrypt(Plain, "right-passphrase");
        encrypted[^1] ^= 0xFF;

        Assert.ThrowsException<BundleException>(() => BundleCrypto.Decrypt(encrypted, "right-passphrase"));
    }

    [TestMethod]
    public void Decrypt_APlainBundle_PassesItThrough()
    {
        Assert.IsFalse(BundleCrypto.IsEncrypted(Plain));
        CollectionAssert.AreEqual(Plain, BundleCrypto.Decrypt(Plain, null));
    }

    [TestMethod]
    public void Decrypt_APlainBundleWithAPassphrase_ExplainsTheMismatch()
    {
        var ex = Assert.ThrowsException<BundleException>(() => BundleCrypto.Decrypt(Plain, "passphrase"));
        StringAssert.Contains(ex.Message, "not encrypted");
    }
}
