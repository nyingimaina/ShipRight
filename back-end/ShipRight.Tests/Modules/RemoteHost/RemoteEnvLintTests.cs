using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.RemoteHost;

namespace ShipRight.Tests.Modules.RemoteHost;

/// <summary>Prepare warns (never fails) about env values that would stop ShipRight from starting.</summary>
[TestClass]
public class RemoteEnvLintTests
{
    private const string Good =
        "MYSQL_ROOT_PASSWORD=a\nMYSQL_PASSWORD=b\nSHIPRIGHT__DB_CONNECTION=Server=db;Password=b\n" +
        "SHIPRIGHT_DOMAIN=ship.example.com\nSHIPRIGHT__CORS_ORIGINS=https://ship.example.com";

    [TestMethod]
    public void AGoodEnv_HasNoWarnings() => Assert.AreEqual(0, RemoteEnvLint.Warnings(Good).Count);

    [TestMethod]
    [DataRow("SHIPRIGHT__CORS_ORIGINS=http://localhost:5200")]
    [DataRow("SHIPRIGHT__CORS_ORIGINS=https://a.example.com,http://127.0.0.1:3000")]
    [DataRow("SHIPRIGHT__PUBLIC_URL=http://localhost:5200")]
    public void LocalhostInCorsOrPublicUrl_IsFlagged_BecauseCloudModeRefusesToStart(string line)
    {
        var w = RemoteEnvLint.Warnings(Good + "\n" + line);
        Assert.IsTrue(w.Any(x => x.Contains("localhost") && x.Contains("will not start")), string.Join("|", w));
    }

    [TestMethod]
    [DataRow("MYSQL_ROOT_PASSWORD")]
    [DataRow("MYSQL_PASSWORD")]
    [DataRow("SHIPRIGHT__DB_CONNECTION")]
    public void MissingOrEmptyRequiredValues_AreFlagged(string key)
    {
        var without = string.Join("\n", Good.Split('\n').Where(l => !l.StartsWith(key + "=")));
        Assert.IsTrue(RemoteEnvLint.Warnings(without).Any(x => x.Contains(key)));
        var empty = Good.Replace($"{key}=", $"{key}=\n#");
        Assert.IsTrue(RemoteEnvLint.Warnings(empty).Any(x => x.Contains(key)));
    }

    [TestMethod]
    public void WarningsNeverContainValues_SoNoSecretIsPrinted()
    {
        var w = string.Join("\n", RemoteEnvLint.Warnings("MYSQL_PASSWORD=supersecret\nSHIPRIGHT__CORS_ORIGINS=http://localhost"));
        Assert.IsFalse(w.Contains("supersecret"));
    }

    [TestMethod]
    public void Command_PrintsEachWarning_OrAnOk_AndNeverFails()
    {
        var bad = RemoteEnvLint.BuildCommand("SHIPRIGHT__CORS_ORIGINS=http://localhost");
        StringAssert.Contains(bad, "[settings] WARNING");
        StringAssert.EndsWith(bad.TrimEnd(), "true");
        StringAssert.Contains(RemoteEnvLint.BuildCommand(Good), "[settings] OK");
        Assert.IsFalse(RemoteEnvLint.BuildCommand("X='y'").Contains("''"));   // quoting stays valid
    }
}
