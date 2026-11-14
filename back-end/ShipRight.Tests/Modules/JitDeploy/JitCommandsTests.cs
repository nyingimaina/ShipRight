using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.JitDeploy;

namespace ShipRight.Tests.Modules.JitDeploy;

[TestClass]
public class JitCommandsTests
{
    private static readonly JitTarget Target = new("h", "u", "/k", "/home/deploy/shop");
    private static readonly ComposeContext Ctx = new(Target, "shop");
    private static readonly IReadOnlyList<TagBinding> Bindings =
        [new("API_TAG", "shop/api"), new("WEB_TAG", "shop/web")];
    private const string Tag = "r42-ab8b4a9";
    private const string Env = "export API_TAG='shop/api:r42-ab8b4a9' WEB_TAG='shop/web:r42-ab8b4a9'";

    // Commands that must never appear in anything this feature sends: they delete things we did not ask it to.
    private static readonly string[] Forbidden =
        ["image prune", "system prune", "volume prune", "volume rm", "container prune", "builder prune", "rm -rf", " down", "--volumes", "-v "];

    private static void AssertSafe(string cmd)
    {
        foreach (var f in Forbidden) Assert.IsFalse(cmd.Contains(f), $"'{f}' must never be sent: {cmd}");
        Assert.IsFalse(cmd.Contains("ShipRight", StringComparison.OrdinalIgnoreCase), "nothing may name the deployer");
        Assert.IsFalse(Regex.IsMatch(cmd, @"docker (image )?rm(i)? .*-f"), "no forced removal");
    }

    // ── preflight ────────────────────────────────────────────────────────────

    [TestMethod]
    public void CheckDocker_VerifiesDockerAndCompose_AndExplainsInPlainLanguage()
    {
        var c = JitCommands.CheckDocker();
        StringAssert.Contains(c, "docker info");
        StringAssert.Contains(c, "docker compose version");
        StringAssert.Contains(c, "exit 1");
        StringAssert.Contains(c, "[preflight]");
        AssertSafe(c);
    }

    [TestMethod]
    public void CheckDisk_PrintsFreeSpace_RefusesBelowTheMinimum_AndNeverPrunes()
    {
        var c = JitCommands.CheckDisk(Target, 3000);
        StringAssert.Contains(c, "df -Pm '/home/deploy/shop'");
        StringAssert.Contains(c, "-lt 3000");
        StringAssert.Contains(c, "Free disk space");
        StringAssert.Contains(c, "exit 1");
        AssertSafe(c);
    }

    [TestMethod]
    public void CheckDisk_RejectsANonPositiveMinimum()
    {
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => JitCommands.CheckDisk(Target, 0));
    }

    [TestMethod]
    public void CheckGit_TriesTheRemoteWithATimeout_AndNeverEchoesTheUrl()
    {
        var url = "https://user:secret-token@github.com/o/r.git";
        var c = JitCommands.CheckGit(url);
        StringAssert.Contains(c, "timeout 30 git ls-remote --heads -- 'https://user:secret-token@github.com/o/r.git'");
        var echoed = c[(c.IndexOf("||", StringComparison.Ordinal))..];
        Assert.IsFalse(echoed.Contains("secret-token"), "the failure message must not leak credentials");
        StringAssert.Contains(c, "[preflight]");
        AssertSafe(c);
    }

    [TestMethod]
    public void CheckGit_RejectsOptionLikeUrls()
    {
        Assert.ThrowsException<ArgumentException>(() => JitCommands.CheckGit("--upload-pack=touch /tmp/x"));
    }

    [TestMethod]
    public void EnsureImagesExist_ChecksEachOne_AndNamesTheMissingOne()
    {
        var c = JitCommands.EnsureImagesExist(["shop/api:r41-aaaaaaa", "shop/web:r41-aaaaaaa"]);
        StringAssert.Contains(c, "docker image inspect 'shop/api:r41-aaaaaaa'");
        StringAssert.Contains(c, "docker image inspect 'shop/web:r41-aaaaaaa'");
        StringAssert.Contains(c, "[preflight]");
        StringAssert.Contains(c, "exit 1");
        AssertSafe(c);
    }

    [TestMethod]
    public void EnsureImagesExist_WithNothingToCheck_Succeeds()
    {
        Assert.AreEqual("true", JitCommands.EnsureImagesExist([]));
    }

    // ── git ──────────────────────────────────────────────────────────────────

    [TestMethod]
    public void SyncRepo_ClonesWhenThereIsNoCheckout_ThenFetches()
    {
        var c = JitCommands.SyncRepo(Target, "git@github.com:o/r.git");
        var iClone = c.IndexOf("git clone", StringComparison.Ordinal);
        var iFetch = c.IndexOf("fetch", StringComparison.Ordinal);
        Assert.IsTrue(iClone > 0 && iFetch > iClone, c);
        StringAssert.Contains(c, "[ -d '/home/deploy/shop/repo/.git' ]");
        StringAssert.Contains(c, "git clone --quiet -- 'git@github.com:o/r.git' '/home/deploy/shop/repo'");
        StringAssert.Contains(c, "fetch --prune --tags --quiet origin");
        AssertSafe(c);
    }

    [TestMethod]
    public void SyncRepo_CreatesTheParentDirectoryFirst()
    {
        var c = JitCommands.SyncRepo(Target, "git@github.com:o/r.git");
        Assert.IsTrue(c.IndexOf("mkdir -p '/home/deploy/shop'", StringComparison.Ordinal) < c.IndexOf("git clone", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ResolveBranch_PrintsTheCommitOfTheRemoteBranch()
    {
        var c = JitCommands.ResolveBranch(Target, "release/1.2");
        StringAssert.Contains(c, "git -C '/home/deploy/shop/repo' rev-parse --verify --quiet 'refs/remotes/origin/release/1.2^{commit}'");
        AssertSafe(c);
    }

    [TestMethod]
    public void ResolveCommit_PrintsTheFullShaOfACommit()
    {
        var c = JitCommands.ResolveCommit(Target, "AB8B4A9");
        StringAssert.Contains(c, "rev-parse --verify --quiet 'ab8b4a9^{commit}'");
    }

    [DataTestMethod]
    [DataRow("master; rm -rf /")]
    [DataRow("$(id)")]
    [DataRow("--upload-pack=x")]
    [DataRow("a b")]
    public void ResolveBranch_RejectsUnsafeBranches(string branch)
    {
        Assert.ThrowsException<ArgumentException>(() => JitCommands.ResolveBranch(Target, branch));
    }

    [TestMethod]
    public void Checkout_DiscardsLocalChanges_ThenDetachesAtTheExactCommit()
    {
        var c = JitCommands.Checkout(Target, "ab8b4a9f3c1d2e5f6a7b8c9d0e1f2a3b4c5d6e7f");
        var iReset = c.IndexOf("reset --hard", StringComparison.Ordinal);
        var iClean = c.IndexOf("clean -fdx", StringComparison.Ordinal);
        var iCheckout = c.IndexOf("checkout --detach", StringComparison.Ordinal);
        Assert.IsTrue(iReset > 0 && iClean > iReset && iCheckout > iClean, c);
        StringAssert.Contains(c, "'ab8b4a9f3c1d2e5f6a7b8c9d0e1f2a3b4c5d6e7f'");
        StringAssert.Contains(c, ".gitmodules");
        StringAssert.Contains(c, "submodule update --init --recursive");
        Assert.IsFalse(c.Contains("'/home/deploy/shop' "), "the clean must only ever run inside the repo folder");
    }

    [TestMethod]
    public void Checkout_NeverCleansOutsideTheRepoFolder()
    {
        var c = JitCommands.Checkout(Target, "ab8b4a9");
        foreach (Match m in Regex.Matches(c, @"clean -fdx"))
            Assert.IsTrue(c[..m.Index].Contains("-C '/home/deploy/shop/repo'") || c[..m.Index].Contains("cd '/home/deploy/shop/repo'"));
    }

    // ── compose ──────────────────────────────────────────────────────────────

    [TestMethod]
    public void ComposeConfig_RendersTheFileAsJson_WithTheTagsExported()
    {
        var c = JitCommands.ComposeConfig(Ctx, Bindings, Tag);
        StringAssert.StartsWith(c, "cd '/home/deploy/shop/repo' && " + Env);
        StringAssert.Contains(c, "docker compose -p 'shop' config --format json");
        AssertSafe(c);
    }

    [TestMethod]
    public void AnEnvFile_IsPassedToEveryComposeCommand()
    {
        var ctx = Ctx with { EnvFile = "/home/deploy/shop/.env" };
        foreach (var c in new[]
        {
            JitCommands.ComposeConfig(ctx, Bindings, Tag),
            JitCommands.Build(ctx, ["api"], Bindings, Tag),
            JitCommands.StopService(ctx, "api", 30, Bindings, Tag),
            JitCommands.StartService(ctx, "api", Bindings, Tag),
            JitCommands.ProbeService(ctx, "api", Bindings, Tag),
        })
            StringAssert.Contains(c, "--env-file '/home/deploy/shop/.env'");
    }

    [TestMethod]
    public void WithoutAnEnvFile_NoEnvFileFlagIsSent()
    {
        Assert.IsFalse(JitCommands.Build(Ctx, ["api"], Bindings, Tag).Contains("--env-file"));
    }

    [TestMethod]
    public void Build_BuildsOnlyTheNamedServices_WithPlainProgress()
    {
        var c = JitCommands.Build(Ctx, ["api", "web"], Bindings, Tag);
        StringAssert.StartsWith(c, "cd '/home/deploy/shop/repo' && " + Env);
        StringAssert.Contains(c, "docker compose -p 'shop' build --progress=plain 'api' 'web'");
        AssertSafe(c);
    }

    [TestMethod]
    public void Build_NeverPullsOrForcesNoCache_SoLayerCachingSpeedsUpRebuilds()
    {
        var c = JitCommands.Build(Ctx, ["api"], Bindings, Tag);
        Assert.IsFalse(c.Contains("--no-cache"));
    }

    [TestMethod]
    public void Build_CanRunAtALowerPriority_SoItDoesNotStarveRunningContainers()
    {
        var c = JitCommands.Build(Ctx, ["api"], Bindings, Tag, niceLevel: 10);
        StringAssert.Contains(c, "nice -n 10 docker compose");
        Assert.IsFalse(JitCommands.Build(Ctx, ["api"], Bindings, Tag).Contains("nice"));
    }

    [DataTestMethod]
    [DataRow(-1)]
    [DataRow(20)]
    public void Build_RejectsAnInvalidNiceLevel(int nice)
    {
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => JitCommands.Build(Ctx, ["api"], Bindings, Tag, nice));
    }

    [TestMethod]
    public void Build_WithNoServices_Throws()
    {
        Assert.ThrowsException<ArgumentException>(() => JitCommands.Build(Ctx, [], Bindings, Tag));
    }

    [TestMethod]
    public void StopService_StopsOnlyThatServiceWithTheGracePeriod_AndNeverRemovesIt()
    {
        var c = JitCommands.StopService(Ctx, "web", 30, Bindings, Tag);
        StringAssert.Contains(c, "docker compose -p 'shop' stop -t 30 'web'");
        AssertSafe(c);
    }

    [TestMethod]
    public void StopService_RejectsANegativeGracePeriod()
    {
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => JitCommands.StopService(Ctx, "web", -1, Bindings, Tag));
    }

    [TestMethod]
    public void StartService_StartsOnlyThatService_WithoutBuildingPullingOrTouchingItsDependencies()
    {
        var c = JitCommands.StartService(Ctx, "api", Bindings, Tag);
        StringAssert.Contains(c, "docker compose -p 'shop' up -d --no-deps --no-build --pull never 'api'");
        AssertSafe(c);
    }

    [TestMethod]
    public void ProbeService_PrintsStateHealthAndRestartCountForEachContainer()
    {
        var c = JitCommands.ProbeService(Ctx, "api", Bindings, Tag);
        StringAssert.Contains(c, "docker compose -p 'shop' ps -a -q 'api'");
        StringAssert.Contains(c, "docker inspect -f");
        StringAssert.Contains(c, "{{.State.Status}}|");
        StringAssert.Contains(c, "{{.RestartCount}}");
        AssertSafe(c);
    }

    [TestMethod]
    public void TagExports_CoverEveryBinding_WithTheReleaseTag()
    {
        var c = JitCommands.StartService(Ctx, "api", Bindings, Tag);
        StringAssert.Contains(c, Env);
    }

    [TestMethod]
    public void NoBindings_MeansNoExport()
    {
        var c = JitCommands.StopService(Ctx, "api", 30, [], Tag);
        Assert.IsFalse(c.Contains("export"));
        StringAssert.StartsWith(c, "cd '/home/deploy/shop/repo' && docker compose");
    }

    [TestMethod]
    public void ARequestedTagThatIsNotAReleaseTag_IsRejected()
    {
        Assert.ThrowsException<ArgumentException>(() => JitCommands.Build(Ctx, ["api"], Bindings, "latest"));
        Assert.ThrowsException<ArgumentException>(() => JitCommands.Build(Ctx, ["api"], Bindings, "r1-abcdef1; id"));
    }

    [DataTestMethod]
    [DataRow("api; rm -rf /")]
    [DataRow("$(id)")]
    [DataRow("a b")]
    [DataRow("-x")]
    public void EveryServiceName_IsValidatedBeforeItReachesTheShell(string service)
    {
        Assert.ThrowsException<ArgumentException>(() => JitCommands.StartService(Ctx, service, Bindings, Tag));
        Assert.ThrowsException<ArgumentException>(() => JitCommands.StopService(Ctx, service, 30, Bindings, Tag));
        Assert.ThrowsException<ArgumentException>(() => JitCommands.ProbeService(Ctx, service, Bindings, Tag));
        Assert.ThrowsException<ArgumentException>(() => JitCommands.Build(Ctx, [service], Bindings, Tag));
    }

    [TestMethod]
    public void BindingsWithAnUnsafeVariableOrImage_AreRejected()
    {
        Assert.ThrowsException<ArgumentException>(() => JitCommands.Build(Ctx, ["api"], [new("API_TAG; id", "shop/api")], Tag));
        Assert.ThrowsException<ArgumentException>(() => JitCommands.Build(Ctx, ["api"], [new("API_TAG", "shop/api'; id #")], Tag));
    }

    [TestMethod]
    public void AProjectNameOrEnvFileOrDirectoryThatIsUnsafe_IsRejected()
    {
        Assert.ThrowsException<ArgumentException>(() => JitCommands.Build(Ctx with { Project = "Shop; id" }, ["api"], Bindings, Tag));
        Assert.ThrowsException<ArgumentException>(() => JitCommands.Build(Ctx with { EnvFile = "relative/.env" }, ["api"], Bindings, Tag));
        Assert.ThrowsException<ArgumentException>(() => JitCommands.SyncRepo(new JitTarget("h", "u", "/k", "/a/../b"), "git@h:o/r.git"));
    }

    [TestMethod]
    public void ADirectoryWithASpaceOrQuote_IsQuotedSafely()
    {
        var t = new JitTarget("h", "u", "/k", "/srv/my apps/o'brien");
        var c = JitCommands.CheckDisk(t, 1000);
        StringAssert.Contains(c, @"'/srv/my apps/o'\''brien'");
    }

    // ── images ───────────────────────────────────────────────────────────────

    [TestMethod]
    public void ListImages_AndListContainerImages_AreReadOnly()
    {
        StringAssert.Contains(JitCommands.ListImages(), "docker image ls --format '{{.Repository}}:{{.Tag}}'");
        StringAssert.Contains(JitCommands.ListContainerImages(), "docker ps -a --format '{{.Image}}'");
        AssertSafe(JitCommands.ListImages());
        AssertSafe(JitCommands.ListContainerImages());
    }

    [TestMethod]
    public void RemoveImages_RemovesEachByExactReference_NeverForced_AndNeverFailsTheDeploy()
    {
        var c = JitCommands.RemoveImages(["shop/api:r1-aaaaaaa", "shop/web:r1-aaaaaaa"]);
        StringAssert.Contains(c, "docker image rm 'shop/api:r1-aaaaaaa'");
        StringAssert.Contains(c, "docker image rm 'shop/web:r1-aaaaaaa'");
        StringAssert.EndsWith(c.TrimEnd(), "true");
        AssertSafe(c);
    }

    [TestMethod]
    public void RemoveImages_RefusesAnythingThatIsNotAReleaseTaggedImage()
    {
        Assert.ThrowsException<ArgumentException>(() => JitCommands.RemoveImages(["shop/api:latest"]));
        Assert.ThrowsException<ArgumentException>(() => JitCommands.RemoveImages(["shop/api"]));
        Assert.ThrowsException<ArgumentException>(() => JitCommands.RemoveImages(["shop/api:r1-aaaaaaa; id"]));
    }

    [TestMethod]
    public void RemoveImages_WithNothingToRemove_IsANoOp()
    {
        Assert.AreEqual("true", JitCommands.RemoveImages([]));
    }

    // ── parsers ──────────────────────────────────────────────────────────────

    [TestMethod]
    public void ProbeParser_ReadsOneResultPerContainer()
    {
        var r = ProbeParser.Parse("running|healthy|0\nrestarting||3\n");
        Assert.AreEqual(2, r.Count);
        Assert.AreEqual(new ProbeResult(ContainerState.Running, HealthStatus.Healthy, 0), r[0]);
        Assert.AreEqual(new ProbeResult(ContainerState.Restarting, HealthStatus.None, 3), r[1]);
    }

    [TestMethod]
    public void ProbeParser_WithNoOutput_MeansNoContainer()
    {
        Assert.AreEqual(0, ProbeParser.Parse("").Count);
        Assert.AreEqual(0, ProbeParser.Parse("\n \n").Count);
    }

    [DataTestMethod]
    [DataRow("garbage")]
    [DataRow("running|healthy")]
    [DataRow("running|healthy|many")]
    [DataRow("flying|healthy|0")]
    public void ProbeParser_RejectsMalformedLines(string line)
    {
        Assert.ThrowsException<FormatException>(() => ProbeParser.Parse(line));
    }

    [TestMethod]
    public void ImageListParser_DropsUntaggedEntries_AndBlankLines()
    {
        var r = ImageListParser.Parse(["shop/api:r1-aaaaaaa", "<none>:<none>", "", "  shop/web:latest  ", "shop/x:<none>"]);
        CollectionAssert.AreEqual(new[] { "shop/api:r1-aaaaaaa", "shop/web:latest" }, r.ToArray());
    }

    [TestMethod]
    public void ReleasesFrom_GroupsOurRepositoriesByReleaseTag()
    {
        var refs = new[]
        {
            "shop/api:r1-aaaaaaa", "shop/web:r1-aaaaaaa", "shop/api:r2-bbbbbbb", "shop/web:r2-bbbbbbb",
            "shop/api:latest", "other/thing:r9-ccccccc", "redis:7",
        };
        var releases = ImageListParser.ReleasesFrom(refs, ["shop/api", "shop/web"]);
        CollectionAssert.AreEqual(new[] { "r1-aaaaaaa", "r2-bbbbbbb" }, releases.Select(r => r.Tag).ToArray());
        CollectionAssert.AreEquivalent(new[] { "shop/api:r1-aaaaaaa", "shop/web:r1-aaaaaaa" }, releases[0].Images.ToArray());
    }

    [TestMethod]
    public void ReleasesFrom_HandlesRegistryHostsWithPorts()
    {
        var releases = ImageListParser.ReleasesFrom(["reg.local:5000/shop/api:r3-ddddddd"], ["reg.local:5000/shop/api"]);
        Assert.AreEqual("r3-ddddddd", releases.Single().Tag);
    }
}
