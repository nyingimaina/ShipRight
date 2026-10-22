using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ShipRight.Tests.Deployment;

/// <summary>
/// The Docker image is built on Alpine (linux-musl). These guard the inputs that broke a release:
/// a lockfile pruned on Windows lost the Linux SWC binaries, and a crashed `npm ci` still let the build continue.
/// </summary>
[TestClass]
public class DockerBuildInputsTests
{
    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "docker-compose.server.yml")))
            dir = dir.Parent;
        Assert.IsNotNull(dir, "repo root not found");
        return Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
    }

    [DataTestMethod]
    [DataRow("@next/swc-linux-x64-musl")]
    [DataRow("@next/swc-linux-x64-gnu")]
    [DataRow("@next/swc-linux-arm64-musl")]
    public void Lockfile_KeepsTheLinuxNextCompilerBinaries(string pkg)
    {
        var lockfile = File.ReadAllText(RepoFile("front-end", "package-lock.json"));
        StringAssert.Contains(lockfile, $"\"node_modules/{pkg}\"",
            "npm install on Windows pruned the Linux optional packages; restore them (see Dockerfile)");
    }

    [TestMethod]
    public void Dockerfile_FailsTheInstallStep_WhenNpmCiLeavesNoNextBinary()
    {
        var install = File.ReadAllLines(RepoFile("Dockerfile")).First(l => l.TrimStart().StartsWith("RUN npm ci"));
        StringAssert.Contains(install, "test -x node_modules/.bin/next");
    }

    [TestMethod]
    public void Dockerfile_PrintsTheNpmDebugLogOnFailure_SoTheRealCauseIsVisible()
    {
        var install = File.ReadAllLines(RepoFile("Dockerfile")).First(l => l.TrimStart().StartsWith("RUN npm ci"));
        StringAssert.Contains(install, "/root/.npm/_logs");
        StringAssert.Contains(install, "exit 1");
    }

    [TestMethod]
    public void Dockerfile_RetriesNpmFetchesOnFlakyNetworks()
    {
        var install = File.ReadAllLines(RepoFile("Dockerfile")).First(l => l.TrimStart().StartsWith("RUN npm ci"));
        StringAssert.Contains(install, "--fetch-retries=");
        StringAssert.Contains(install, "--fetch-timeout=");
    }

    [TestMethod]
    public void Dockerfile_SkipsNpmAuditAndFund_ToAvoidNetworkHangs()
    {
        var install = File.ReadAllLines(RepoFile("Dockerfile")).First(l => l.TrimStart().StartsWith("RUN npm ci"));
        StringAssert.Contains(install, "--no-audit");
        StringAssert.Contains(install, "--no-fund");
    }
}
