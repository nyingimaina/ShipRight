using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Shared;

namespace ShipRight.Tests.Shared;

[TestClass]
public class StaticAssetCachingTests : IDisposable
{
    private readonly string _webRoot;

    public StaticAssetCachingTests()
    {
        _webRoot = Path.Combine(Path.GetTempPath(), $"sr_static_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_webRoot, "_next", "static", "chunks"));
        Directory.CreateDirectory(Path.Combine(_webRoot, "projects"));
        File.WriteAllText(Path.Combine(_webRoot, "index.html"), "<html>home</html>");
        File.WriteAllText(Path.Combine(_webRoot, "projects", "index.html"), "<html>projects</html>");
        File.WriteAllText(Path.Combine(_webRoot, "version.json"), "{\"version\":\"1.0.0\"}");
        File.WriteAllText(Path.Combine(_webRoot, "_next", "static", "chunks", "4864-abc123.js"), "console.log(1)");
    }

    public void Dispose() => Directory.Delete(_webRoot, recursive: true);

    // ── The rule ─────────────────────────────────────────────────────────────

    [DataTestMethod]
    [DataRow("/_next/static/chunks/4864-ad482b9cc85d1d73.js")]
    [DataRow("/_next/static/css/app-1f2e3d.css")]
    [DataRow("/_next/static/BUILDID123/_buildManifest.js")]
    public void HashedBuildAssets_AreCachedForAYearAndNeverRevalidated(string path)
    {
        Assert.AreEqual("public, max-age=31536000, immutable", StaticAssetCaching.CacheControlFor(path));
    }

    [DataTestMethod]
    [DataRow("/")]
    [DataRow("/index.html")]
    [DataRow("/projects/")]
    [DataRow("/projects/index.html")]
    [DataRow("/404.html")]
    [DataRow("/version.json")]
    [DataRow("/favicon.ico")]
    public void EverythingElse_MustBeRevalidatedEveryTime(string path)
    {
        Assert.AreEqual("no-cache", StaticAssetCaching.CacheControlFor(path));
    }

    // ── The headers really reach the browser (real pipeline, in memory) ──────

    private async Task<HttpClient> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Environment.WebRootFileProvider = new PhysicalFileProvider(_webRoot);
        var app = builder.Build();
        app.UseDefaultFiles();
        app.UseStaticFiles(StaticAssetCaching.Options());
        app.MapFallbackToFile("index.html", StaticAssetCaching.FallbackOptions());
        await app.StartAsync();
        return app.GetTestClient();
    }

    [TestMethod]
    public async Task ThePageAndItsVersionFile_AreServedWithNoCache()
    {
        using var client = await StartAsync();

        foreach (var url in new[] { "/", "/projects/", "/version.json" })
        {
            var response = await client.GetAsync(url);
            Assert.AreEqual(System.Net.HttpStatusCode.OK, response.StatusCode, url);
            Assert.AreEqual("no-cache", response.Headers.CacheControl?.ToString(), url);
        }
    }

    [TestMethod]
    public async Task AHashedScript_IsServedWithALongImmutableCache()
    {
        using var client = await StartAsync();

        var response = await client.GetAsync("/_next/static/chunks/4864-abc123.js");

        Assert.AreEqual("public, max-age=31536000, immutable", response.Headers.CacheControl?.ToString());
    }

    [TestMethod]
    public async Task TheSinglePageFallback_ForAnUnknownRoute_IsAlsoNoCache()
    {
        using var client = await StartAsync();

        var response = await client.GetAsync("/projects/lattice-backend");

        Assert.AreEqual(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("no-cache", response.Headers.CacheControl?.ToString());
    }

    [TestMethod]
    public async Task AMissingHashedScript_Is404_AndIsNeverGivenALongCache()
    {
        using var client = await StartAsync();

        var response = await client.GetAsync("/_next/static/chunks/gone-0000.js");

        Assert.AreEqual(System.Net.HttpStatusCode.NotFound, response.StatusCode);
        Assert.IsFalse(response.Headers.CacheControl?.ToString().Contains("immutable") ?? false);
    }

    [TestMethod]
    public async Task ARevalidationRequest_StillGetsA304_SoNoCacheIsNotSlow()
    {
        using var client = await StartAsync();
        var first = await client.GetAsync("/");
        var etag = first.Headers.ETag!;

        var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.IfNoneMatch.Add(etag);
        var second = await client.SendAsync(request);

        Assert.AreEqual(System.Net.HttpStatusCode.NotModified, second.StatusCode);
    }
}
