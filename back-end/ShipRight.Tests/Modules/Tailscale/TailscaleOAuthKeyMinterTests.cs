using System.Net;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Tailscale;

namespace ShipRight.Tests.Modules.Tailscale;

[TestClass]
public class TailscaleOAuthKeyMinterTests
{
    private sealed class Handler : HttpMessageHandler
    {
        public List<(string Url, string Body, string? Authorization)> Calls { get; } = [];
        public Func<HttpRequestMessage, HttpResponseMessage>? KeyResponder { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
            Calls.Add((request.RequestUri!.ToString(), body, request.Headers.Authorization?.ToString()));
            if (request.RequestUri!.AbsolutePath.EndsWith("/oauth/token"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"access_token\":\"access-123\"}", Encoding.UTF8, "application/json"),
                };
            }

            return KeyResponder?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"key\":\"tskey-auth-generated\"}", Encoding.UTF8, "application/json"),
            };
        }
    }

    [TestMethod]
    public async Task MintsAKeyWithTheConfiguredOAuthClientAndTags()
    {
        var handler = new Handler();
        var minter = new TailscaleOAuthKeyMinter(new HttpClient(handler));

        var key = await minter.CreateAuthKeyAsync(new TailnetConnection
        {
            Name = "Acme",
            Tailnet = "example.com",
            OAuthClientId = "client-id",
            OAuthClientSecret = "client-secret",
            Tags = "tag:shipright",
        });

        Assert.AreEqual("tskey-auth-generated", key);
        Assert.AreEqual(2, handler.Calls.Count);
        StringAssert.Contains(handler.Calls[0].Body, "client_id=client-id");
        StringAssert.Contains(handler.Calls[0].Body, "client_secret=client-secret");
        Assert.AreEqual("Bearer access-123", handler.Calls[1].Authorization);
        StringAssert.Contains(handler.Calls[1].Url, "/api/v2/tailnet/example.com/keys");
        StringAssert.Contains(handler.Calls[1].Body, "tag:shipright");
        StringAssert.Contains(handler.Calls[1].Body, "ephemeral");
    }

    [TestMethod]
    public async Task RefusesToMintWithoutOAuthCredentials()
    {
        var minter = new TailscaleOAuthKeyMinter(new HttpClient(new Handler()));

        await Assert.ThrowsExceptionAsync<ShipRight.Modules.ServerNetwork.NetworkUnavailableException>(() =>
            minter.CreateAuthKeyAsync(new TailnetConnection { Name = "Acme" }));
    }

    [TestMethod]
    public async Task FallsBackToTheCredentialsDefaultTailnetWhenTheConfiguredNameIsNotFound()
    {
        var handler = new Handler
        {
            KeyResponder = request => request.RequestUri!.AbsolutePath.Contains("/tailnet/-/keys")
                ? Json(HttpStatusCode.OK, "{\"key\":\"tskey-auth-generated\"}")
                : Json(HttpStatusCode.NotFound, "{\"message\":\"not found\"}"),
        };
        var minter = new TailscaleOAuthKeyMinter(new HttpClient(handler));

        var key = await minter.CreateAuthKeyAsync(new TailnetConnection
        {
            Name = "Acme",
            Tailnet = "wrong-name.example",
            OAuthClientId = "client-id",
            OAuthClientSecret = "client-secret",
        });

        Assert.AreEqual("tskey-auth-generated", key);
        Assert.AreEqual(3, handler.Calls.Count, "token, the configured tailnet, then the default");
        StringAssert.Contains(handler.Calls[1].Url, "/tailnet/wrong-name.example/keys");
        StringAssert.Contains(handler.Calls[2].Url, "/tailnet/-/keys");
    }

    [TestMethod]
    public async Task NotFoundExplainsTheTailnetAndTheAuthKeysScope()
    {
        var handler = new Handler
        {
            KeyResponder = _ => Json(HttpStatusCode.NotFound, "{\"message\":\"not found\"}"),
        };
        var minter = new TailscaleOAuthKeyMinter(new HttpClient(handler));

        var error = await Assert.ThrowsExceptionAsync<ShipRight.Modules.ServerNetwork.NetworkUnavailableException>(() =>
            minter.CreateAuthKeyAsync(new TailnetConnection
            {
                Name = "Acme",
                Tailnet = "wrong-name.example",
                OAuthClientId = "client-id",
                OAuthClientSecret = "client-secret",
            }));

        StringAssert.Contains(error.Message, "could not find the tailnet");
        StringAssert.Contains(error.Message, "Settings → General");
        StringAssert.Contains(error.Message, "Auth keys");
        StringAssert.Contains(error.Message, "Set");
    }

    [TestMethod]
    public async Task ForbiddenDoesNotRetryAndMentionsTheCredentialScope()
    {
        var handler = new Handler
        {
            KeyResponder = _ => Json(HttpStatusCode.Forbidden, "{\"message\":\"insufficient scope\"}"),
        };
        var minter = new TailscaleOAuthKeyMinter(new HttpClient(handler));

        var error = await Assert.ThrowsExceptionAsync<ShipRight.Modules.ServerNetwork.NetworkUnavailableException>(() =>
            minter.CreateAuthKeyAsync(new TailnetConnection
            {
                Name = "Acme",
                Tailnet = "example.com",
                OAuthClientId = "client-id",
                OAuthClientSecret = "client-secret",
            }));

        Assert.AreEqual(2, handler.Calls.Count, "token + one key request, no fallback");
        StringAssert.Contains(error.Message, "Auth keys");
        StringAssert.Contains(error.Message, "insufficient scope");
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };
}
