using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Builds;

namespace ShipRight.Tests.Modules.Builds;

/// <summary>
/// UpdateTagsAsync understands both `image: name:1.2.3` and `image: name:${VAR:-1.2.3}` (default-valued tag
/// variable), so a server can override the tag from .env (rollback) while the pipeline keeps the default current.
/// </summary>
[TestClass]
public class DockerComposeTagVariableTests
{
    private const string Ecr = "080632633137.dkr.ecr.us-east-2.amazonaws.com/ship-right/app";

    private static async Task<string> RunAsync(string yaml, Dictionary<string, string> map)
    {
        var path = Path.Combine(Path.GetTempPath(), $"compose-{Guid.NewGuid():N}.yml");
        await File.WriteAllTextAsync(path, yaml);
        try
        {
            await DockerComposeUpdater.UpdateTagsAsync(path, map);
            return await File.ReadAllTextAsync(path);
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public async Task UpdatesTheDefaultInsideATagVariable_KeepingTheVariable()
    {
        var yaml = $"services:\n  app:\n    image: {Ecr}:${{SHIPRIGHT_APP_TAG:-4.4.6}}\n";
        var result = await RunAsync(yaml, new() { [Ecr] = "4.4.7" });
        StringAssert.Contains(result, $"image: {Ecr}:${{SHIPRIGHT_APP_TAG:-4.4.7}}");
        Assert.IsFalse(result.Contains("4.4.6"));
    }

    [TestMethod]
    public async Task StillUpdatesLiteralTags()
    {
        var result = await RunAsync($"    image: {Ecr}:4.4.6\n", new() { [Ecr] = "4.4.7" });
        StringAssert.Contains(result, $"image: {Ecr}:4.4.7");
    }

    [TestMethod]
    public async Task HandlesQuotedValues()
    {
        var result = await RunAsync($"    image: \"{Ecr}:${{TAG:-1.0.0}}\"\n    image: '{Ecr}:1.0.0'\n", new() { [Ecr] = "2.0.0" });
        StringAssert.Contains(result, $"image: \"{Ecr}:${{TAG:-2.0.0}}\"");
        StringAssert.Contains(result, $"image: '{Ecr}:2.0.0'");
    }

    [TestMethod]
    public async Task LeavesOtherImagesAndVariablesAlone()
    {
        var yaml = $"    image: mariadb:11\n    image: other/thing:${{OTHER_TAG:-1.2.3}}\n    image: {Ecr}:${{T:-1.0.0}}\n";
        var result = await RunAsync(yaml, new() { [Ecr] = "9.9.9" });
        StringAssert.Contains(result, "image: mariadb:11");
        StringAssert.Contains(result, "image: other/thing:${OTHER_TAG:-1.2.3}");
        StringAssert.Contains(result, $"image: {Ecr}:${{T:-9.9.9}}");
    }

    [TestMethod]
    public async Task IsIdempotent()
    {
        var yaml = $"    image: {Ecr}:${{T:-1.0.0}}\n";
        var once = await RunAsync(yaml, new() { [Ecr] = "2.0.0" });
        var twice = await RunAsync(once, new() { [Ecr] = "2.0.0" });
        Assert.AreEqual(once, twice);
    }

    [TestMethod]
    public async Task PreservesTheFilesLineEndings_SoGitDiffsStayMinimal()
    {
        var lf   = await RunAsync($"a: 1\n    image: {Ecr}:1.0.0\nb: 2\n", new() { [Ecr] = "2.0.0" });
        var crlf = await RunAsync($"a: 1\r\n    image: {Ecr}:1.0.0\r\nb: 2\r\n", new() { [Ecr] = "2.0.0" });
        Assert.IsFalse(lf.Contains('\r'), "an LF file must stay LF");
        Assert.AreEqual(3, crlf.Split("\r\n").Length - 1, "a CRLF file must stay CRLF");
        Assert.IsFalse(crlf.Replace("\r\n", "").Contains('\n'));
    }

    [TestMethod]
    public async Task VariableWithoutADefault_IsLeftAlone()
    {
        var yaml = $"    image: {Ecr}:${{T}}\n";
        Assert.AreEqual(yaml, await RunAsync(yaml, new() { [Ecr] = "2.0.0" }));
    }
}
