using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ShipRight.Tests.Deployment;

/// <summary>
/// The front-end image installs with "npm ci". npm 11 (node:24-slim) refuses a lockfile that is
/// missing optional platform packages, so the committed lockfile must stay in sync with package.json.
/// </summary>
[TestClass]
public class FrontEndPackageLockTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "front-end", "package.json")))
            dir = dir.Parent;
        Assert.IsNotNull(dir, "front-end/package.json not found above the test directory");
        return dir.FullName;
    }

    private static JsonDocument Json(string relativePath) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "front-end", relativePath)));

    [TestMethod]
    public void LockfileRecordsTheCurrentPackageVersion()
    {
        using var pkg = Json("package.json");
        using var lockFile = Json("package-lock.json");

        var expected = pkg.RootElement.GetProperty("version").GetString();
        Assert.AreEqual(expected, lockFile.RootElement.GetProperty("version").GetString(),
            "top-level lockfile version is stale");
        Assert.AreEqual(expected,
            lockFile.RootElement.GetProperty("packages").GetProperty("").GetProperty("version").GetString(),
            "root package version in the lockfile is stale");
    }

    [TestMethod]
    public void LockfileRespectsPackageOverrides()
    {
        using var pkg = Json("package.json");
        using var lockFile = Json("package-lock.json");

        if (!pkg.RootElement.TryGetProperty("overrides", out var overrides))
            return;

        var packages = lockFile.RootElement.GetProperty("packages");
        foreach (var item in overrides.EnumerateObject())
        {
            var path = $"node_modules/{item.Name}";
            Assert.IsTrue(packages.TryGetProperty(path, out var locked),
                $"overridden package '{item.Name}' is missing from the lockfile");
            Assert.AreEqual(item.Value.GetString(), locked.GetProperty("version").GetString(),
                $"override '{item.Name}' is not reflected in the locked version");
        }
    }

    [TestMethod]
    public void LockfileIncludesAllOptionalPlatformPackages()
    {
        using var lockFile = Json("package-lock.json");
        var packages = lockFile.RootElement.GetProperty("packages");

        var installed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in packages.EnumerateObject())
            installed.Add(entry.Name);

        var missing = new List<string>();
        foreach (var entry in packages.EnumerateObject())
        {
            if (!entry.Value.TryGetProperty("optionalDependencies", out var optional))
                continue;

            foreach (var dep in optional.EnumerateObject())
            {
                var suffix = $"node_modules/{dep.Name}";
                var present = installed.Any(p => p == suffix || p.EndsWith('/' + suffix, StringComparison.Ordinal));
                if (!present)
                    missing.Add($"{dep.Name} (required by {entry.Name})");
            }
        }

        Assert.AreEqual(0, missing.Count,
            "npm ci fails under npm 11 when optional packages are absent from the lockfile:\n"
            + string.Join("\n", missing.Take(20)));
    }
}
