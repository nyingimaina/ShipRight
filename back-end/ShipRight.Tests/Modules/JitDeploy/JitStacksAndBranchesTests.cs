using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.JitDeploy;

namespace ShipRight.Tests.Modules.JitDeploy;

/// <summary>Reading the stacks already on the server (so adopting one is safe) and the branches of a repository (so none is typed).</summary>
[TestClass]
public class JitStacksAndBranchesTests
{
    private const string Dir = "/home/u/shop";

    // ── stacks already on the server ─────────────────────────────────────────

    [TestMethod]
    public void ListCommand_OnlyReads()
    {
        var c = JitStacks.ListCommand();
        StringAssert.Contains(c, "docker compose ls");
        StringAssert.Contains(c, "--format json");
        foreach (var bad in new[] { " down", " stop", " rm ", " prune", "shipright" })
            Assert.IsFalse(c.Contains(bad, StringComparison.OrdinalIgnoreCase), bad);
    }

    [TestMethod]
    public void Parse_ReadsNamesStatusAndComposeFiles()
    {
        var stacks = JitStacks.Parse(["[{\"Name\":\"shop\",\"Status\":\"running(2)\",\"ConfigFiles\":\"/home/u/shop/repo/docker-compose.yml\"},",
                                      "{\"Name\":\"blog\",\"Status\":\"exited(1)\",\"ConfigFiles\":\"/a/c.yml, /a/d.yml\"}]"]);
        Assert.AreEqual(2, stacks.Count);
        Assert.AreEqual("shop", stacks[0].Name);
        Assert.AreEqual("running(2)", stacks[0].Status);
        CollectionAssert.AreEqual(new[] { "/a/c.yml", "/a/d.yml" }, stacks[1].ConfigFiles.ToArray());
    }

    [DataTestMethod]
    [DataRow("[]")]
    [DataRow("")]
    [DataRow("not json at all")]
    [DataRow("{\"Name\":\"x\"}")]
    public void Parse_AnythingElse_IsNoStacks_NeverAnError(string text) =>
        Assert.AreEqual(0, JitStacks.Parse([text]).Count);

    private static ComposeStack Stack(string name, params string[] files) => new(name, "running(1)", files);

    [TestMethod]
    public void NoStacksOnTheServer_IsNothingToWorryAbout()
    {
        var r = JitStacks.Evaluate([], "shop", Dir);
        Assert.AreEqual(StackMatch.None, r.Match);
        Assert.AreEqual(0, r.Warnings.Count);
    }

    [TestMethod]
    public void ASameNamedStack_ThatBuildOnServerAlreadyRuns_IsFine()
    {
        var r = JitStacks.Evaluate([Stack("shop", Dir + "/repo/docker-compose.yml")], "shop", Dir);
        Assert.AreEqual(StackMatch.Same, r.Match);
        Assert.AreEqual(0, r.Warnings.Count);
        Assert.IsNull(r.SuggestedName);
    }

    [TestMethod]
    public void ASameNamedStack_StartedFromAnotherFolder_WarnsThatRelativePathsMove()
    {
        foreach (var file in new[] { Dir + "/docker-compose.yml", "/opt/other/compose.yaml" })
        {
            var r = JitStacks.Evaluate([Stack("shop", file)], "shop", Dir);
            Assert.AreEqual(StackMatch.Same, r.Match, file);
            var w = r.Warnings.Single();
            StringAssert.Contains(w, "relative");
            StringAssert.Contains(w, Dir + "/repo");
            StringAssert.Contains(w, "volumes");
        }
    }

    [TestMethod]
    public void AStackFromTheProjectFolder_UnderAnotherName_WarnsOfASecondCopy()
    {
        var r = JitStacks.Evaluate([Stack("shop-old", Dir + "/docker-compose.yml")], "shop", Dir);
        Assert.AreEqual(StackMatch.Different, r.Match);
        var w = r.Warnings.Single();
        StringAssert.Contains(w, "second copy");
        StringAssert.Contains(w, "shop-old");
        StringAssert.Contains(w, "'shop'");
        Assert.AreEqual("shop-old", r.SuggestedName, "the stack to take over is offered by name");
    }

    [TestMethod]
    public void AStackFromAnotherFolderAndName_IsJustListed()
    {
        var r = JitStacks.Evaluate([Stack("blog", "/srv/blog/docker-compose.yml")], "shop", Dir);
        Assert.AreEqual(StackMatch.None, r.Match);
        Assert.AreEqual(0, r.Warnings.Count);
        Assert.AreEqual("blog", r.Stacks.Single().Name);
    }

    [TestMethod]
    public void TheTrailingSlashOnTheFolder_DoesNotMatter()
    {
        var r = JitStacks.Evaluate([Stack("shop-old", Dir + "/docker-compose.yml")], "shop", Dir + "/");
        Assert.AreEqual(StackMatch.Different, r.Match);
    }

    // ── branches ─────────────────────────────────────────────────────────────

    [TestMethod]
    public void BranchCommand_AsksOnlyForHeadsAndTheDefault_WithATimeout_AndNeverPrompts()
    {
        var c = JitBranches.Command("git@github.com:o/r.git");
        StringAssert.Contains(c, "git ls-remote --symref");
        StringAssert.Contains(c, "'refs/heads/*'");
        StringAssert.Contains(c, "timeout 30");
        StringAssert.Contains(c, "GIT_TERMINAL_PROMPT=0");
        StringAssert.Contains(c, "-- 'git@github.com:o/r.git'");
        Assert.IsFalse(c.Contains("shipright", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void BranchCommand_NeverEchoesTheAddress_InItsFailureMessage()
    {
        var c = JitBranches.Command("git@github.com:secret-org/r.git");
        var message = c[c.IndexOf("echo", StringComparison.Ordinal)..];
        Assert.IsFalse(message.Contains("secret-org"));
    }

    [DataTestMethod]
    [DataRow("--upload-pack=touch /tmp/x")]
    [DataRow("https://me:pw@github.com/o/r.git")]
    [DataRow("https://ghp_tok@github.com/o/r.git")]
    [DataRow("git@github.com:o/r .git")]
    [DataRow("")]
    public void BranchCommand_RefusesUnsafeAddresses_AndAnyEmbeddedSecret(string url) =>
        Assert.ThrowsException<ArgumentException>(() => JitBranches.Command(url));

    [TestMethod]
    public void Parse_FindsTheDefaultFirst_ThenTheRestInOrder_IgnoringTags()
    {
        var b = JitBranches.Parse([
            "ref: refs/heads/main\tHEAD",
            "1111111111111111111111111111111111111111\tHEAD",
            "2222222222222222222222222222222222222222\trefs/heads/main",
            "3333333333333333333333333333333333333333\trefs/heads/feature/x",
            "4444444444444444444444444444444444444444\trefs/heads/dev",
            "5555555555555555555555555555555555555555\trefs/tags/v1",
        ]);
        Assert.AreEqual("main", b.DefaultBranch);
        CollectionAssert.AreEqual(new[] { "main", "dev", "feature/x" }, b.Branches.ToArray());
    }

    [TestMethod]
    public void Parse_WithNoDefaultLine_StillListsTheBranches()
    {
        var b = JitBranches.Parse(["2222222222222222222222222222222222222222\trefs/heads/zeta", "3333333333333333333333333333333333333333\trefs/heads/alpha"]);
        Assert.IsNull(b.DefaultBranch);
        CollectionAssert.AreEqual(new[] { "alpha", "zeta" }, b.Branches.ToArray());
    }

    [TestMethod]
    public void Parse_OfNoiseOrNothing_IsEmpty()
    {
        Assert.AreEqual(0, JitBranches.Parse(["", "garbage", "[branches] Cannot read"]).Branches.Count);
        Assert.AreEqual(0, JitBranches.Parse([]).Branches.Count);
    }
}
