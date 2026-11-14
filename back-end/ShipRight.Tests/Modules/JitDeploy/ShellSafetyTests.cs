using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.JitDeploy;

namespace ShipRight.Tests.Modules.JitDeploy;

/// <summary>Everything that ends up inside a remote shell command goes through these two gates first.</summary>
[TestClass]
public class ShellSafetyTests
{
    // ── ShellQuote ───────────────────────────────────────────────────────────

    [DataTestMethod]
    [DataRow("abc", "'abc'")]
    [DataRow("", "''")]
    [DataRow("with space", "'with space'")]
    [DataRow("it's", @"'it'\''s'")]
    [DataRow("''", @"''\'''\'''")]
    [DataRow("$(rm -rf /)", "'$(rm -rf /)'")]
    [DataRow("`id`", "'`id`'")]
    [DataRow("a;b&&c|d>e", "'a;b&&c|d>e'")]
    [DataRow("$HOME", "'$HOME'")]
    public void Single_WrapsInSingleQuotes_AndEscapesEmbeddedQuotes(string raw, string expected)
    {
        Assert.AreEqual(expected, ShellQuote.Single(raw));
    }

    [TestMethod]
    public void Single_KeepsNewlinesInsideTheQuotes()
    {
        Assert.AreEqual("'a\nb'", ShellQuote.Single("a\nb"));
    }

    [TestMethod]
    public void Single_RejectsNulCharacters()
    {
        Assert.ThrowsException<ArgumentException>(() => ShellQuote.Single("a\0b"));
    }

    [TestMethod]
    public void Single_RejectsNull()
    {
        Assert.ThrowsException<ArgumentNullException>(() => ShellQuote.Single(null!));
    }

    // ── JitNames ─────────────────────────────────────────────────────────────

    [DataTestMethod]
    [DataRow("api")]
    [DataRow("web-2")]
    [DataRow("my_service.v2")]
    [DataRow("A1")]
    public void Service_AcceptsComposeServiceNames(string v) => Assert.AreEqual(v, JitNames.Service(v));

    [DataTestMethod]
    [DataRow("")]
    [DataRow("a b")]
    [DataRow("-rf")]
    [DataRow("a;b")]
    [DataRow("a$(x)")]
    [DataRow("a/b")]
    [DataRow(".hidden")]
    [DataRow("a\nb")]
    public void Service_RejectsAnythingElse(string v) => Assert.ThrowsException<ArgumentException>(() => JitNames.Service(v));

    [DataTestMethod]
    [DataRow("master")]
    [DataRow("release/1.2")]
    [DataRow("feature/x_y-z")]
    public void Branch_AcceptsOrdinaryBranchNames(string v) => Assert.AreEqual(v, JitNames.Branch(v));

    [DataTestMethod]
    [DataRow("")]
    [DataRow("-delete")]
    [DataRow("--upload-pack=x")]
    [DataRow("a b")]
    [DataRow("a..b")]
    [DataRow("a;rm")]
    [DataRow("a/")]
    [DataRow("/a")]
    [DataRow("a.lock")]
    [DataRow("a@{1}")]
    [DataRow("a~1")]
    [DataRow("a^")]
    [DataRow("a:b")]
    public void Branch_RejectsOptionLikeAndUnsafeNames(string v) => Assert.ThrowsException<ArgumentException>(() => JitNames.Branch(v));

    [DataTestMethod]
    [DataRow("ab8b4a9")]
    [DataRow("AB8B4A9F3C1D")]
    [DataRow("ab8b4a9f3c1d2e5f6a7b8c9d0e1f2a3b4c5d6e7f")]
    public void Sha_AcceptsHexOfSevenToSixtyFourCharacters_Lowercased(string v) => Assert.AreEqual(v.ToLowerInvariant(), JitNames.Sha(v));

    [DataTestMethod]
    [DataRow("abc")]
    [DataRow("zzzzzzz")]
    [DataRow("")]
    [DataRow("abcdef1; ls")]
    [DataRow("-abcdef1")]
    public void Sha_RejectsNonHexOrTooShort(string v) => Assert.ThrowsException<ArgumentException>(() => JitNames.Sha(v));

    [DataTestMethod]
    [DataRow("owner/api")]
    [DataRow("registry.example.com:5000/team/app")]
    [DataRow("api")]
    [DataRow("owner/api:r42-ab8b4a9")]
    public void ImageRef_AcceptsDockerReferences(string v) => Assert.AreEqual(v, JitNames.ImageRef(v));

    [DataTestMethod]
    [DataRow("")]
    [DataRow("-x")]
    [DataRow("Owner/Api")]       // docker repositories are lower case
    [DataRow("a b")]
    [DataRow("a;b")]
    [DataRow("a$(x)")]
    public void ImageRef_RejectsAnythingElse(string v) => Assert.ThrowsException<ArgumentException>(() => JitNames.ImageRef(v));

    [DataTestMethod]
    [DataRow("API_TAG")]
    [DataRow("MY_SERVICE_V2_TAG")]
    public void TagVariable_AcceptsUpperSnakeCase(string v) => Assert.AreEqual(v, JitNames.TagVariable(v));

    [DataTestMethod]
    [DataRow("api_tag")]
    [DataRow("1API")]
    [DataRow("API TAG")]
    [DataRow("API-TAG")]
    [DataRow("")]
    [DataRow("A=B")]
    public void TagVariable_RejectsAnythingElse(string v) => Assert.ThrowsException<ArgumentException>(() => JitNames.TagVariable(v));

    [DataTestMethod]
    [DataRow("shop")]
    [DataRow("my-shop_2")]
    public void Project_AcceptsComposeProjectNames(string v) => Assert.AreEqual(v, JitNames.Project(v));

    [DataTestMethod]
    [DataRow("Shop")]
    [DataRow("")]
    [DataRow("-shop")]
    [DataRow("a b")]
    public void Project_RejectsAnythingElse(string v) => Assert.ThrowsException<ArgumentException>(() => JitNames.Project(v));

    [DataTestMethod]
    [DataRow("git@github.com:owner/repo.git")]
    [DataRow("https://github.com/owner/repo.git")]
    [DataRow("ssh://git@host:2222/owner/repo.git")]
    public void RepoUrl_AcceptsOrdinaryGitUrls(string v) => Assert.AreEqual(v, JitNames.RepoUrl(v));

    [DataTestMethod]
    [DataRow("")]
    [DataRow("--upload-pack=touch /tmp/x")]
    [DataRow("-oProxyCommand=x")]
    [DataRow("https://h/r.git\nrm -rf /")]
    [DataRow("ext::sh -c id")]
    public void RepoUrl_RejectsOptionLikeControlAndExtTransportUrls(string v) => Assert.ThrowsException<ArgumentException>(() => JitNames.RepoUrl(v));

    [DataTestMethod]
    [DataRow("/home/deploy/shop")]
    [DataRow("/srv/apps/my app")]
    public void Path_AcceptsAbsolutePaths(string v) => Assert.AreEqual(v, JitNames.Path(v));

    [DataTestMethod]
    [DataRow("")]
    [DataRow("relative/dir")]
    [DataRow("~/shop")]
    [DataRow("/a/../etc")]
    [DataRow("/a\nb")]
    [DataRow("/")]
    public void Path_RejectsRelativeTraversalControlAndRoot(string v) => Assert.ThrowsException<ArgumentException>(() => JitNames.Path(v));

    // ── JitTarget layout ─────────────────────────────────────────────────────

    [TestMethod]
    public void Target_PutsTheRepoAndTheStateSideBySide_SoCleaningTheRepoCannotDeleteState()
    {
        var t = new JitTarget("h", "u", "/k", "/home/deploy/shop/");
        Assert.AreEqual("/home/deploy/shop", t.ProjectDir);
        Assert.AreEqual("/home/deploy/shop/repo", t.RepoDir);
        Assert.AreEqual("/home/deploy/shop/.deploy-state", t.StateDir);
        Assert.AreEqual("/home/deploy/shop/.deploy-state/state.json", t.StateFile);
    }
}
