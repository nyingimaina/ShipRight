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
        Assert.AreEqual(2, images);           // db + app
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
            DockerComposeUpdater.UpdateAsync(copy, new Dictionary<string, string> { [AppImage] = "9.9.9" }).GetAwaiter().GetResult();
            StringAssert.Contains(File.ReadAllText(copy), $"image: {AppImage}:9.9.9");
        }
        finally { File.Delete(copy); }
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
