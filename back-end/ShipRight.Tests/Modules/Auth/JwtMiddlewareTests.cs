using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Auth;
using ShipRight.Modules.Auth.Middleware;

namespace ShipRight.Tests.Modules.Auth;

[TestClass]
public class JwtMiddlewareTests
{
    [TestMethod]
    public void IsAnonymousPath_OnlyMatchesRegisteredRoute()
    {
        Assert.IsTrue(JwtMiddleware.IsAnonymousPath("/api/health"));
        Assert.IsFalse(JwtMiddleware.IsAnonymousPath("/api/health/../projects"));
        Assert.IsFalse(JwtMiddleware.IsAnonymousPath("/api/auth/login-extra"));
    }

    [TestMethod]
    public void ForgotPasswordResponse_DoesNotExposeResetToken()
    {
        var response = AuthRouter.BuildForgotPasswordResponse();

        Assert.IsFalse(response.Contains("secret-reset-token", StringComparison.Ordinal));
    }
}
