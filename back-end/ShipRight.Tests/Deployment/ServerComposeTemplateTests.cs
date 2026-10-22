using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Builds;

namespace ShipRight.Tests.Deployment;

/// <summary>
/// docker-compose.server.yml is what the deployment server runs: it only holds the compose repo, so every
/// service must pull an image (no build:), and the pipeline's compose updater must be able to bump the app tag.
/// </summary>
[TestClass]
public class ServerComposeTemplateTests
{
    private const string AppImage = "080632633137.dkr.ecr.us-east-2.amazonaws.com/ship-right/app";

    private static string TemplatePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "docker-compose.server.yml")))
            dir = dir.Parent;
        Assert.IsNotNull(dir, "docker-compose.server.yml not found above the test directory");
        return Path.Combine(dir.FullName, "docker-compose.server.yml");
    }

    [TestMethod]
    public void NoServiceBuildsOnTheServer()
    {
        var text = File.ReadAllText(TemplatePath());
        Assert.IsFalse(text.Split('\n').Any(l => l.TrimStart().StartsWith("build:")), "a deployment server has no Dockerfile to build from");
    }

    [TestMethod]
    public void EveryServiceHasAnImage()
    {
        var lines = File.ReadAllLines(TemplatePath());
        var services = lines.Count(l => l.StartsWith("  ") && !l.StartsWith("   ") && l.TrimEnd().EndsWith(":")
            && !l.TrimStart().StartsWith("shipright-")) - 0;
        var images = lines.Count(l => l.TrimStart().StartsWith("image:"));
        Assert.AreEqual(3, images);           // db + app + caddy
        Assert.IsTrue(services >= images);
    }

    [TestMethod]
    public void DoesNotMountFilesFromTheSourceTree()
    {
        var text = File.ReadAllText(TemplatePath());
        Assert.IsFalse(text.Contains("./back-end"), "the server has no source tree to bind-mount from");
    }

    [TestMethod]
    public void AppImageTagCanBeBumpedByThePipelineUpdater()
    {
        var copy = Path.Combine(Path.GetTempPath(), $"compose-{Guid.NewGuid():N}.yml");
        File.Copy(TemplatePath(), copy);
        try
        {
            DockerComposeUpdater.UpdateTagsAsync(copy, new Dictionary<string, string> { [AppImage] = "9.9.9" }).GetAwaiter().GetResult();
            StringAssert.Contains(File.ReadAllText(copy), $"image: {AppImage}:${{SHIPRIGHT_APP_TAG:-9.9.9}}");
        }
        finally { File.Delete(copy); }
    }

    [TestMethod]
    public void AppTagComesFromAnEnvVariable_WithAPipelineMaintainedDefault_SoAnyVersionCanBeDeployed()
    {
        var line = File.ReadAllLines(TemplatePath()).Single(l => l.TrimStart().StartsWith($"image: {AppImage}"));
        StringAssert.Matches(line, new System.Text.RegularExpressions.Regex(@":\$\{SHIPRIGHT_APP_TAG:-\d+\.\d+\.\d+\}\s*$"));
    }

    [TestMethod]
    public void DatabaseHealthcheck_ToleratesASlowFirstStartOnASmallServer()
    {
        var lines = File.ReadAllLines(TemplatePath());
        int Seconds(string key)
        {
            var l = lines.First(x => x.TrimStart().StartsWith($"{key}:"));
            return int.Parse(System.Text.RegularExpressions.Regex.Match(l, @"\d+").Value);
        }
        Assert.IsTrue(Seconds("timeout") >= 10, "a 5s probe timed out on a memory-starved server");
        Assert.IsTrue(Seconds("start_period") >= 90, "first start initialises the database");
        Assert.IsTrue(Seconds("retries") >= 10);
    }

    [TestMethod]
    public void DatabaseMemoryIsCapped_SoItFitsAlongsideTheAppOnA1GbServer()
    {
        var text = File.ReadAllText(TemplatePath());
        StringAssert.Contains(text, "--innodb-buffer-pool-size=64M");
        StringAssert.Contains(text, "--performance-schema=OFF");
        StringAssert.Contains(text, "--max-connections=50");
    }

    // ── Stable identity: moving the compose folder must never orphan the data ─

    [TestMethod]
    public void ProjectNameIsPinned_SoRenamingTheWorkingFolderKeepsTheSameContainersAndVolumes()
    {
        Assert.IsTrue(File.ReadAllLines(TemplatePath()).Any(l => l == "name: shipright"));
    }

    [TestMethod]
    public void EveryVolumeHasAFixedName()
    {
        var text = File.ReadAllText(TemplatePath());
        foreach (var (key, name) in new[]
        {
            ("shipright-db-data", "shipright-db-data"), ("shipright-app-data", "shipright-app-data"),
            ("caddy-data", "shipright-caddy-data"), ("caddy-config", "shipright-caddy-config"),
        })
            StringAssert.Matches(text, new System.Text.RegularExpressions.Regex($@"(?m)^  {key}:\r?\n    name: {name}\r?$"));
    }

    // ── HTTPS front door (Caddy) ─────────────────────────────────────────────

    private static string Block(string service)
    {
        var lines = File.ReadAllLines(TemplatePath()).ToList();
        var start = lines.FindIndex(l => l == $"  {service}:");
        Assert.IsTrue(start >= 0, $"service '{service}' not found");
        var end = lines.FindIndex(start + 1, l => l.Length > 2 && l.StartsWith("  ") && !l.StartsWith("   ") || (l.Length > 0 && !l.StartsWith(" ")));
        return string.Join("\n", lines.Skip(start).Take((end < 0 ? lines.Count : end) - start));
    }

    [TestMethod]
    public void CaddyTerminatesHttps_OnPorts80And443_WithPersistentCertificates()
    {
        var caddy = Block("caddy");
        StringAssert.Matches(caddy, new System.Text.RegularExpressions.Regex(@"image:\s*caddy:\d"));
        StringAssert.Contains(caddy, "\"80:80\"");
        StringAssert.Contains(caddy, "\"443:443\"");
        StringAssert.Contains(caddy, "caddy-data:/data");   // certificates must survive redeploys (rate limits)
    }

    [TestMethod]
    public void CaddyProxiesTheDomainToTheApp_AndTheDomainIsRequired()
    {
        var caddy = Block("caddy");
        StringAssert.Contains(caddy, "reverse-proxy");
        StringAssert.Contains(caddy, "${SHIPRIGHT_DOMAIN:?");
        StringAssert.Contains(caddy, "app:5200");
        StringAssert.Contains(caddy, "depends_on");
    }

    [TestMethod]
    public void AppIsNotReachableFromTheInternet_OnlyFromTheServerItself()
    {
        var app = Block("app");
        StringAssert.Contains(app, "\"127.0.0.1:5200:5200\"");   // the deploy health check curls localhost:5200
        Assert.IsFalse(app.Contains("\"5200:5200\""), "plain-HTTP port 5200 must not be public");
    }

    [TestMethod]
    public void PublicUrlAndCorsDefaultToTheHttpsDomain()
    {
        var app = Block("app");
        StringAssert.Contains(app, "SHIPRIGHT__PUBLIC_URL: ${SHIPRIGHT__PUBLIC_URL:-https://${SHIPRIGHT_DOMAIN}}");
        StringAssert.Contains(app, "SHIPRIGHT__CORS_ORIGINS: ${SHIPRIGHT__CORS_ORIGINS:-https://${SHIPRIGHT_DOMAIN}}");
    }

    [TestMethod]
    public void RequiresOnlyEnvVariablesTheAppStillReads()
    {
        var text = File.ReadAllText(TemplatePath());
        foreach (var obsolete in new[] { "SHIPRIGHT__JWT_KEY", "SHIPRIGHT__ADMIN_EMAIL", "SHIPRIGHT__ADMIN_PASSWORD" })
            Assert.IsFalse(text.Contains(obsolete), $"{obsolete} was removed by the identity refactor");
        StringAssert.Contains(text, "SHIPRIGHT__DB_CONNECTION");
    }
}
