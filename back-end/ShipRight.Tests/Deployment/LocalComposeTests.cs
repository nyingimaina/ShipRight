using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.JitDeploy;

namespace ShipRight.Tests.Deployment;

/// <summary>
/// docker-compose.yml is the build-on-target-machine variant: it must stay usable with no .env at all,
/// while still offering a Caddy HTTPS front door when a domain is supplied.
/// </summary>
[TestClass]
public class LocalComposeTests
{
    private static string ComposePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "docker-compose.yml")))
            dir = dir.Parent;
        Assert.IsNotNull(dir, "docker-compose.yml not found above the test directory");
        return Path.Combine(dir.FullName, "docker-compose.yml");
    }

    private static string Text() => File.ReadAllText(ComposePath());

    private static string[] Lines() => File.ReadAllLines(ComposePath());

    private static string Block(string service)
    {
        var lines = Lines().ToList();
        var start = lines.FindIndex(l => l == $"  {service}:");
        Assert.IsTrue(start >= 0, $"service '{service}' not found");
        var end = lines.FindIndex(start + 1, l =>
            (l.Length > 2 && l.StartsWith("  ") && !l.StartsWith("   "))
            || (l.Length > 0 && !l.StartsWith(" ")));
        return string.Join("\n", lines.Skip(start).Take((end < 0 ? lines.Count : end) - start));
    }

    // ── Current behaviour: these must keep working ────────────────────────────

    [TestMethod]
    public void AppBuildsFromTheLocalDockerfile()
    {
        var app = Block("app");
        StringAssert.Contains(app, "build:");
        StringAssert.Contains(app, "context: .");
        StringAssert.Contains(app, "dockerfile: Dockerfile");
        Assert.IsFalse(app.Contains("080632633137.dkr.ecr."), "the local stack builds on the target machine");
    }

    [TestMethod]
    public void FreshDatabaseIsInitialisedWithTheSchema()
    {
        StringAssert.Contains(Block("db"),
            "./back-end/ShipRight.Server/Database/schema.sql:/docker-entrypoint-initdb.d/01-schema.sql:ro");
    }

    [TestMethod]
    public void AppStillServesPort5200()
    {
        StringAssert.Contains(Block("app"), "5200:5200");
    }

    // ── Intended behaviour ────────────────────────────────────────────────────

    [TestMethod]
    public void ProjectNameIsPinnedSoVolumesKeepTheirNames()
    {
        Assert.IsTrue(Lines().Any(l => l == "name: shipright"), "project name must not depend on the folder name");
        foreach (var key in new[] { "shipright-db-data", "shipright-app-data", "caddy-data", "caddy-config" })
        {
            StringAssert.Matches(Text(), new Regex($@"(?m)^  {key}:\r?$"));
            Assert.IsFalse(Regex.IsMatch(Text(), $@"(?m)^  {key}:\r?\n    name:"),
                $"{key} should use the pinned project prefix so existing data is adopted");
        }
    }

    [TestMethod]
    public void OfficialImagesUsePinnedVersions()
    {
        StringAssert.Contains(Block("db"), "image: mariadb:11.8.9");
        StringAssert.Contains(Block("caddy"), "image: caddy:2.11.9-alpine");
    }

    [TestMethod]
    public void DatabaseStartsTolerantlyAndFitsASmallServer()
    {
        var db = Block("db");
        StringAssert.Contains(db, "--innodb-buffer-pool-size=64M");
        StringAssert.Contains(db, "--performance-schema=OFF");
        StringAssert.Contains(db, "--max-connections=50");

        var lines = db.Split('\n');
        int Seconds(string key)
        {
            var line = lines.First(l => l.TrimStart().StartsWith($"{key}:"));
            return int.Parse(Regex.Match(line, @"\d+").Value);
        }
        Assert.IsTrue(Seconds("timeout") >= 10);
        Assert.IsTrue(Seconds("start_period") >= 90);
        Assert.IsTrue(Seconds("retries") >= 10);
    }

    [TestMethod]
    public void AppHasAHealthcheckAndIsBoundToLoopback()
    {
        var app = Block("app");
        StringAssert.Contains(app, "healthcheck:");
        StringAssert.Contains(app, "/api/health");
        StringAssert.Contains(app, "\"127.0.0.1:5200:5200\"");
        Assert.IsFalse(app.Contains("- \"5200:5200\""), "plain HTTP must not be published on all interfaces");
    }

    [TestMethod]
    public void NoAppSettingIsRequiredAtComposeParseTime()
    {
        foreach (var line in Lines().Where(l => Regex.IsMatch(l, @"^\s+SHIPRIGHT__[A-Z_]+:")))
            Assert.IsFalse(line.Contains(":?"), $"a usable local default is missing: {line}");
    }

    [TestMethod]
    public void SafeLocalDefaultsAreUsedForDatabaseAndPublicUrl()
    {
        var text = Text();
        StringAssert.Contains(text, "Password=${MYSQL_PASSWORD:-shipright}");
        StringAssert.Contains(text, "https://${SHIPRIGHT_DOMAIN:-localhost}");
    }

    [TestMethod]
    public void CaddyServesPlainHttpLocallyAndHttpsForADomain()
    {
        var caddy = Block("caddy");
        StringAssert.Contains(caddy, "reverse-proxy");
        StringAssert.Contains(caddy, "app:5200");
        StringAssert.Contains(caddy, "SHIPRIGHT_DOMAIN");
        StringAssert.Contains(caddy, ":8080");
        StringAssert.Contains(caddy, "\"80:80\"");
        StringAssert.Contains(caddy, "\"443:443\"");
        StringAssert.Contains(caddy, "\"443:443/udp\"");
        StringAssert.Contains(caddy, "\"8080:8080\"");
        StringAssert.Contains(caddy, "depends_on");
    }

    [TestMethod]
    public void CaddyCertificatesAndConfigArePersistent()
    {
        var caddy = Block("caddy");
        StringAssert.Contains(caddy, "caddy-data:/data");
        StringAssert.Contains(caddy, "caddy-config:/config");
    }

    // ── ShipRight can deploy this file ───────────────────────────────────────

    [TestMethod]
    public void AppImageCarriesTheTagVariableShipRightInjects()
    {
        StringAssert.Contains(Block("app"), "image: ${APP_TAG:-shipright/app:latest}");
    }

    [TestMethod]
    public void ShipRightReadsThisFileAsABuildOnServerDeploy()
    {
        var reading = ComposeServiceReader.Read(ComposePath(), "ShipRight");
        var app = reading.Services.Single(s => s.Name == "app");

        Assert.AreEqual("APP_TAG", app.TagVariable);
        Assert.AreEqual("shipright/app", app.ImageRef);
        Assert.IsTrue(app.Built, "the service has its own build section");
        Assert.IsNull(app.Note, "no image-line fix should be needed");
        Assert.AreEqual("Dockerfile", app.Dockerfile);
        Assert.AreEqual(".", app.Context);
        CollectionAssert.AreEquivalent(new[] { "db", "caddy" }, reading.LeftAlone.ToArray());
    }
}
