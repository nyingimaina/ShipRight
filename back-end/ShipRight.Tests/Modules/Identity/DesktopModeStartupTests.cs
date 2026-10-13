using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ShipRight.Tests.Modules.Identity;

// R11: desktop/local mode needs no sign-in and no cloud-only settings, and still reads its own data.
[TestClass]
public class DesktopModeStartupTests
{
    private sealed class DesktopFactory : WebApplicationFactory<Program>
    {
        private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "shipright-desktop-test-" + Guid.NewGuid().ToString("N")[..8]);
        private readonly Dictionary<string, string?> _saved = new();

        public string DataDir => _dataDir;

        public DesktopFactory()
        {
            // None of the cloud-only settings, and no Google or mail settings.
            foreach (var name in ShipRight.Server.CloudConfiguration.SettingNames)
                Set(name, null);
            Set("SHIPRIGHT_DATA_DIR", _dataDir);
        }

        private void Set(string name, string? value)
        {
            _saved[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder) { }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (!disposing) return;
            foreach (var (name, value) in _saved) Environment.SetEnvironmentVariable(name, value);
            try { Directory.Delete(_dataDir, recursive: true); } catch { /* temp folder, best effort */ }
        }
    }

    [TestMethod]
    public async Task DesktopMode_StartsWithNoCloudSettings_AndReadsDataWithoutSigningIn()
    {
        // R11, R13, BOOT-001
        using var factory = new DesktopFactory();
        using var client = factory.CreateClient();

        var projects = await client.GetAsync("/api/projects");
        var health = await client.GetAsync("/api/health");

        Assert.AreEqual(HttpStatusCode.OK, projects.StatusCode);
        Assert.AreEqual("[]", (await projects.Content.ReadAsStringAsync()).Trim());
        Assert.AreEqual(HttpStatusCode.OK, health.StatusCode);
        StringAssert.Contains(await health.Content.ReadAsStringAsync(), "\"mode\":\"Desktop\"");
    }

    [TestMethod]
    public async Task DesktopMode_HasNoSignInRoutes_AndNeverAsksForOne()
    {
        // R11: a machine that once had a local sign-in is not asked to sign in; nothing answers 401.
        using var factory = new DesktopFactory();
        using var client = factory.CreateClient();

        foreach (var url in new[] { "/api/builds", "/api/servers", "/api/resources/scripts", "/api/scheduler/history" })
            Assert.AreNotEqual(HttpStatusCode.Unauthorized, (await client.GetAsync(url)).StatusCode, url);

        var team = await client.GetAsync("/api/team");
        Assert.AreNotEqual("application/json", team.Content.Headers.ContentType?.MediaType, "no team endpoint exists in desktop mode");
    }

    [TestMethod]
    public async Task DesktopMode_KeepsEveryStoreInsideTheConfiguredDataDirectory()
    {
        // R11: no store may fall back to the real ~/.shipright, so a smoke run cannot change real data.
        using var factory = new DesktopFactory();
        using var client = factory.CreateClient();

        Assert.AreEqual(HttpStatusCode.OK, (await client.GetAsync("/api/health")).StatusCode);

        foreach (var relative in new[]
                 {
                     "projects.db", "builds.db",
                     Path.Combine("scheduler", "backup-history.db"),
                     Path.Combine("watch-branch", "watch-branch.db"),
                 })
            Assert.IsTrue(File.Exists(Path.Combine(factory.DataDir, relative)),
                $"{relative} must live under SHIPRIGHT_DATA_DIR, not the real ~/.shipright");
    }
}
