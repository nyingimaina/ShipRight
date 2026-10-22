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
        var install = File.ReadAllLines(RepoFile("Dockerfile")).First(l => l.TrimStart().StartsWith("RUN") && l.Contains("npm ci"));
        StringAssert.Contains(install, "test -x node_modules/.bin/next");
    }

    [TestMethod]
    public void Dockerfile_PrintsTheNpmDebugLogOnFailure_SoTheRealCauseIsVisible()
    {
        var install = File.ReadAllLines(RepoFile("Dockerfile")).First(l => l.TrimStart().StartsWith("RUN") && l.Contains("npm ci"));
        StringAssert.Contains(install, "/root/.npm/_logs");
        StringAssert.Contains(install, "exit 1");
    }

    [TestMethod]
    public void Dockerfile_RetriesNpmFetchesOnFlakyNetworks()
    {
        var install = File.ReadAllLines(RepoFile("Dockerfile")).First(l => l.TrimStart().StartsWith("RUN") && l.Contains("npm ci"));
        StringAssert.Contains(install, "--fetch-retries=");
        StringAssert.Contains(install, "--fetch-timeout=");
    }

    [TestMethod]
    public void Dockerfile_KeepsTheNpmCacheBetweenAttempts_SoAFlakyNetworkConverges()
    {
        var install = File.ReadAllLines(RepoFile("Dockerfile")).First(l => l.TrimStart().StartsWith("RUN"));
        StringAssert.Contains(install, "--mount=type=cache,target=/root/.npm");
    }

    [TestMethod]
    public void Dockerfile_TriesNpmjsFirst_ThenFallsBackToAMirror_WhenTheRouteToNpmjsIsBroken()
    {
        var text = File.ReadAllText(RepoFile("Dockerfile"));
        var install = File.ReadAllLines(RepoFile("Dockerfile")).First(l => l.TrimStart().StartsWith("RUN") && l.Contains("npm ci"));
        Assert.IsTrue(install.IndexOf("--registry=https://registry.npmjs.org/", StringComparison.Ordinal)
                      < install.IndexOf("NPM_FALLBACK_REGISTRY", StringComparison.Ordinal), "npmjs must be tried first");
        StringAssert.Contains(text, "ARG NPM_FALLBACK_REGISTRY=https://registry.npmmirror.com");
        Assert.AreEqual(2, System.Text.RegularExpressions.Regex.Matches(install, "test -x node_modules/.bin/next").Count,
            "each attempt must verify the install, because npm can exit 0 after crashing");
    }

    [TestMethod]
    public void Lockfile_PinsIntegrityHashes_SoAnyRegistryMirrorIsSafe()
    {
        var lockfile = File.ReadAllText(RepoFile("front-end", "package-lock.json"));
        Assert.IsTrue(System.Text.RegularExpressions.Regex.Matches(lockfile, "\"integrity\": \"sha512-").Count > 500);
    }

    [TestMethod]
    public void Dockerfile_LimitsParallelNpmDownloads_ForLossyLinks()
    {
        var install = File.ReadAllLines(RepoFile("Dockerfile")).First(l => l.TrimStart().StartsWith("RUN") && l.Contains("npm ci"));
        StringAssert.Contains(install, "--maxsockets=");
    }

    [TestMethod]
    public void Dockerfile_SkipsNpmAuditAndFund_ToAvoidNetworkHangs()
    {
        var install = File.ReadAllLines(RepoFile("Dockerfile")).First(l => l.TrimStart().StartsWith("RUN") && l.Contains("npm ci"));
        StringAssert.Contains(install, "--no-audit");
        StringAssert.Contains(install, "--no-fund");
    }
}
