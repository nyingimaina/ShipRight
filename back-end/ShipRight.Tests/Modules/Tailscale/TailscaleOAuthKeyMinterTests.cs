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

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
            Calls.Add((request.RequestUri!.ToString(), body, request.Headers.Authorization?.ToString()));
            return request.RequestUri!.AbsolutePath.EndsWith("/oauth/token")
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"access_token\":\"access-123\"}", Encoding.UTF8, "application/json"),
                }
                : new HttpResponseMessage(HttpStatusCode.OK)
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
}
