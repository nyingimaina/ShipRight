using System.Net;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Git;

namespace ShipRight.Tests.Modules.Git;

[TestClass]
public class GitHubProviderTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(respond(request));
        }
    }

    private static (GitHubProvider Provider, Handler Handler) Create(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new Handler(respond);
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") };
        return (new GitHubProvider(client), handler);
    }

    private static HttpResponseMessage Json(string text, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(text, Encoding.UTF8, "application/json") };

    private static GitUrlInfo Url() => GitUrl.Parse("https://github.com/acme/shop")!;

    [TestMethod]
    public async Task ReadsTheRepository()
    {
        var (provider, _) = Create(_ => Json(
            """{"id":123,"default_branch":"main","private":true,"html_url":"https://github.com/acme/shop"}"""));

        var info = await provider.GetRepoAsync(Url(), null, default);

        Assert.AreEqual("main", info.DefaultBranch);
        Assert.IsTrue(info.Private);
        Assert.AreEqual(123, info.RepoId);
        Assert.AreEqual("https://github.com/acme/shop", info.WebUrl);
    }

    [TestMethod]
    public async Task ReadsBranches()
    {
        var (provider, _) = Create(_ => Json(
            """[{"name":"main","commit":{"sha":"abc"}},{"name":"dev","commit":{"sha":"def"}}]"""));

        var branches = await provider.GetBranchesAsync(Url(), null, default);

        Assert.AreEqual(2, branches.Count);
        Assert.AreEqual("main", branches[0].Name);
        Assert.AreEqual("abc", branches[0].Sha);
    }

    [TestMethod]
    public async Task ReadsTheTree()
    {
        var (provider, _) = Create(_ => Json(
            """{"tree":[{"path":"docker-compose.yml","type":"blob","size":10},{"path":"src","type":"tree"}]}"""));

        var tree = await provider.GetTreeAsync(Url(), "main", null, default);

        Assert.AreEqual(2, tree.Count);
        Assert.AreEqual("blob", tree[0].Type);
        Assert.AreEqual(10, tree[0].Size);
    }

    [TestMethod]
    public async Task ReadsAFile()
    {
        var (provider, _) = Create(_ => Json("""{"content":"aGVsbG8="}"""));

        var text = await provider.GetFileAsync(Url(), "main", "README.md", null, default);

        Assert.AreEqual("hello", text);
    }

    [TestMethod]
    public async Task SendsTheTokenWhenGiven()
    {
        var (provider, handler) = Create(_ => Json("""{"default_branch":"main"}"""));

        await provider.GetRepoAsync(Url(), "tok", default);

        Assert.AreEqual("tok", handler.LastRequest!.Headers.Authorization?.Parameter);
    }

    [TestMethod]
    public async Task ExplainsANotFoundRepository()
    {
        var (provider, _) = Create(_ => Json("{}", HttpStatusCode.NotFound));

        var error = await Assert.ThrowsExceptionAsync<GitProviderException>(
            () => provider.GetRepoAsync(Url(), null, default));

        StringAssert.Contains(error.Message.ToLowerInvariant(), "not found");
    }

    [TestMethod]
    public async Task ExplainsARateLimit()
    {
        var (provider, _) = Create(_ =>
        {
            var response = Json("{}", HttpStatusCode.Forbidden);
            response.Headers.Add("X-RateLimit-Remaining", "0");
            return response;
        });

        var error = await Assert.ThrowsExceptionAsync<GitProviderException>(
            () => provider.GetRepoAsync(Url(), null, default));

        StringAssert.Contains(error.Message.ToLowerInvariant(), "rate limit");
    }
}
