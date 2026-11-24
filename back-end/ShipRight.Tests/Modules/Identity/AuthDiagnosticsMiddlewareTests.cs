using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Identity;

namespace ShipRight.Tests.Modules.Identity;

[TestClass]
public class AuthDiagnosticsMiddlewareTests
{
    [TestMethod]
    public async Task LogsAuthRequestAndResponse_WithCookieNamesButNeverValues()
    {
        var logger = new CapturingLogger();
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/auth/google/complete";
        context.Request.Method = "GET";
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("shipright.jaribio.biz");
        context.Request.Headers["X-Forwarded-Proto"] = "https";
        context.Request.Headers.Cookie = "shipright.auth=a-secret-token-value";
        var middleware = new AuthDiagnosticsMiddleware(_ =>
        {
            context.Response.StatusCode = 302;
            return Task.CompletedTask;
        }, logger);

        await middleware.InvokeAsync(context);

        Assert.AreEqual(2, logger.Messages.Count);
        StringAssert.Contains(logger.Messages[0], "/api/auth/google/complete");
        StringAssert.Contains(logger.Messages[0], "scheme=https");
        StringAssert.Contains(logger.Messages[0], "shipright.auth");
        StringAssert.Contains(logger.Messages[1], "status=302");
        Assert.IsFalse(string.Join("\n", logger.Messages).Contains("a-secret-token-value"));
    }

    private sealed class CapturingLogger : ILogger<AuthDiagnosticsMiddleware>
    {
        public List<string> Messages { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => Scope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));

        private sealed class Scope : IDisposable
        {
            public static readonly Scope Instance = new();
            public void Dispose() { }
        }
    }
}
