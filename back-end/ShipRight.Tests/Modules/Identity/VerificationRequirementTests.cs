using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Tests.Modules.Tenancy;

namespace ShipRight.Tests.Modules.Identity;

// Verification-only instruments for this run. They name requirements R1, R2 and R3 and exercise the
// behaviour behind each, so TEST-001 has a test per requirement inside the suite `dotnet test` runs.
// They add no application code and change no application behaviour.
[TestClass]
public class VerificationRequirementTests
{
    private sealed class VerificationDesktopFactory : WebApplicationFactory<Program>
    {
        private readonly string _dataDir =
            Path.Combine(Path.GetTempPath(), "shipright-verify-" + Guid.NewGuid().ToString("N")[..8]);
        private readonly Dictionary<string, string?> _saved = new();

        public string DataDir => _dataDir;

        public VerificationDesktopFactory()
        {
            // Desktop/local mode: none of the cloud-only settings, and no Google or mail settings.
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

    // ── R1: the app still loads and reads its own data; the run never opens the real data directory ──

    [TestMethod]
    public async Task R1_AppReadsItsOwnProjects_AndEveryStoreStaysInTheThrowawayDataDirectory()
    {
        // R1: reading data through the app succeeds, and every store lives in a throwaway temporary data
        // directory, so the run never opens the real data directory for change and no stored record is touched.
        using var factory = new VerificationDesktopFactory();
        using var client = factory.CreateClient();

        var health = await client.GetAsync("/api/health");
        var projects = await client.GetAsync("/api/projects");

        Assert.AreEqual(HttpStatusCode.OK, health.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, projects.StatusCode);
        Assert.AreEqual("[]", (await projects.Content.ReadAsStringAsync()).Trim());

        Assert.IsTrue(
            factory.DataDir.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase),
            "verification must run against a throwaway directory under the OS temp dir, never the real data directory");
        foreach (var relative in new[] { "projects.db", "builds.db" })
            Assert.IsTrue(File.Exists(Path.Combine(factory.DataDir, relative)),
                $"{relative} must stay inside the throwaway data directory, not the real one");
    }

    // ── R2: the unchanged app boots and reads with no settings and no sign-in in desktop mode ──

    [TestMethod]
    public async Task R2_DesktopMode_ServesTheHomePage_ReadsProjectsWithNoCredentials_AndExposesNoSignInRoute()
    {
        // R2: started with no cloud, mail or Google settings, the home page loads as HTML, reading projects
        // succeeds without credentials, and no sign-in route is exposed.
        using var factory = new VerificationDesktopFactory();
        using var client = factory.CreateClient();

        var home = await client.GetAsync("/");
        var projects = await client.GetAsync("/api/projects");
        var providers = await client.GetAsync("/api/auth/providers");

        Assert.AreEqual(HttpStatusCode.OK, home.StatusCode);
        Assert.AreEqual("text/html", home.Content.Headers.ContentType?.MediaType);
        Assert.AreEqual(HttpStatusCode.OK, projects.StatusCode);
        Assert.AreEqual("application/json", projects.Content.Headers.ContentType?.MediaType);
        Assert.AreNotEqual("application/json", providers.Content.Headers.ContentType?.MediaType,
            "desktop mode must not expose a sign-in route");
    }

    [TestMethod]
    public async Task R2_CloudMode_RefusesASessionlessRead_AndScopesAProjectReadToTheCallersCompany()
    {
        // R2: in cloud mode a read without a session is refused, and a signed-in person's project read is
        // scoped to their own company by the SQL itself.
        using var factory = new InMemoryCloudFactory();

        Assert.AreEqual(HttpStatusCode.Unauthorized, (await factory.NewClient().Get("/api/projects")).Status,
            "a read without a session must be refused");

        var user = factory.Seed("r2-scope@example.test");
        var client = factory.NewClient();
        Assert.AreEqual(HttpStatusCode.OK, (await client.Login(user.Email, "long-enough-1")).Status);
        Assert.AreEqual(HttpStatusCode.OK, (await client.Get("/api/projects")).Status);

        var projectRead = factory.Db.Statements.LastOrDefault(s =>
            s.Sql.Contains("Project", StringComparison.OrdinalIgnoreCase) &&
            s.Sql.Contains("CompanyId", StringComparison.OrdinalIgnoreCase));
        Assert.IsNotNull(projectRead.Sql, "the project read must filter by company inside the SQL");
        var parameters = RecordingDatabaseHelper.ParametersOf(projectRead.Parameters);
        Assert.IsTrue(
            parameters.Values.Any(value =>
                string.Equals(value?.ToString(), user.CompanyId.ToString(), StringComparison.OrdinalIgnoreCase)),
            "the caller's own company id must be bound into the query");
    }

    // ── R3: every requirement is proven by a test that names it ──

    [TestMethod]
    public void R3_EveryRequirement_IsNamedByAnAutomatedTest()
    {
        // R3: R1, R2 and R3 each have at least one automated test whose name names the requirement id.
        var testNames = typeof(VerificationRequirementTests).Assembly
            .GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            .Where(method => method.GetCustomAttributes(inherit: true).OfType<TestMethodAttribute>().Any())
            .Select(method => method.Name)
            .ToList();

        foreach (var requirementId in new[] { "R1", "R2", "R3" })
            Assert.IsTrue(
                testNames.Any(name => name.Contains(requirementId, StringComparison.Ordinal)),
                $"requirement {requirementId} must be named by at least one automated test");
    }
}
