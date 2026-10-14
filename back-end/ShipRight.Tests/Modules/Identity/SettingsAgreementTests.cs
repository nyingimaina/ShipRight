using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Server;

namespace ShipRight.Tests.Modules.Identity;

// R13: the example settings file, docker-compose and the app list exactly the same settings.
[TestClass]
public class SettingsAgreementTests
{
    private static readonly string[] Removed =
        ["SHIPRIGHT__JWT_KEY", "SHIPRIGHT__ADMIN_EMAIL", "SHIPRIGHT__ADMIN_PASSWORD"];

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "docker-compose.yml"))) dir = dir.Parent;
        return dir?.FullName ?? throw new FileNotFoundException("docker-compose.yml was not found above the test folder.");
    }

    private static string[] Names(string text, string pattern) =>
        Regex.Matches(text, pattern, RegexOptions.Multiline).Select(m => m.Groups[1].Value).Distinct().OrderBy(n => n).ToArray();

    private static string[] AppSettings => CloudConfiguration.SettingNames.OrderBy(n => n).ToArray();

    [TestMethod]
    public void ExampleFile_ListsExactlyTheAppsSettings()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), ".env.example"));

        CollectionAssert.AreEqual(AppSettings, Names(text, @"^(SHIPRIGHT__[A-Z_]+)="));
    }

    [TestMethod]
    public void DockerCompose_ListsExactlyTheAppsSettings()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "docker-compose.yml"));

        CollectionAssert.AreEqual(AppSettings, Names(text, @"^\s+(SHIPRIGHT__[A-Z_]+):"));
    }

    [TestMethod]
    public void RemovedSettings_AppearNowhereInTheThreeLists()
    {
        var root = RepoRoot();
        var all = File.ReadAllText(Path.Combine(root, ".env.example")) + File.ReadAllText(Path.Combine(root, "docker-compose.yml"))
            + string.Join(' ', CloudConfiguration.SettingNames);

        foreach (var name in Removed) Assert.IsFalse(all.Contains(name), name);
    }

    [TestMethod]
    public void EverySetting_IsReadSomewhereInTheRunningApp()
    {
        // Looked for as a string literal in the server's code, outside the list that declares it.
        var server = Path.Combine(RepoRoot(), "back-end", "ShipRight.Server");
        var code = Directory.EnumerateFiles(server, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !f.EndsWith("CloudConfiguration.cs"))
            .Select(File.ReadAllText)
            .ToList();

        foreach (var name in CloudConfiguration.SettingNames)
            Assert.IsTrue(code.Any(c => c.Contains($"\"{name}\"")), $"{name} is declared but nothing reads it");
    }

    [TestMethod]
    public void ExampleFile_HoldsNoRealLookingSecrets()
    {
        // R13: only obvious placeholders, empty values or example.com addresses.
        var lines = File.ReadAllLines(Path.Combine(RepoRoot(), ".env.example"))
            .Where(l => Regex.IsMatch(l, @"^[A-Z_]+=")).ToList();

        foreach (var line in lines.Where(l => Regex.IsMatch(l, "(PASSWORD|SECRET|KEY)=")))
        {
            var value = line[(line.IndexOf('=') + 1)..];
            Assert.IsTrue(value.Length == 0 || value.Contains("change", StringComparison.OrdinalIgnoreCase), line);
        }

        var connection = lines.Single(l => l.StartsWith("SHIPRIGHT__DB_CONNECTION="));
        StringAssert.Contains(connection, "Password=changeme");
    }
}
