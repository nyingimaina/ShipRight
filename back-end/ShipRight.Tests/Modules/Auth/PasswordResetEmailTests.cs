using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Auth.Services;

namespace ShipRight.Tests.Modules.Auth;

[TestClass]
public class PasswordResetEmailTests
{
    [TestMethod]
    public void BuildResetLink_UsesConfiguredPublicUrlAndToken()
    {
        var link = PasswordResetEmail.BuildResetLink("https://shipright.example.com", "abc123");

        Assert.AreEqual("https://shipright.example.com/reset-password?token=abc123", link);
    }

    [TestMethod]
    public void BuildResetLink_RejectsNonHttpsProductionUrl()
    {
        Assert.ThrowsException<InvalidOperationException>(() =>
            PasswordResetEmail.BuildResetLink("http://shipright.example.com", "abc123"));
    }
}
