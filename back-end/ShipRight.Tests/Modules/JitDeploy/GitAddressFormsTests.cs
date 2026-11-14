using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.JitDeploy;

namespace ShipRight.Tests.Modules.JitDeploy;

/// <summary>The two ways to write one repository's address, with any password or token removed, and a way to hide secrets in text.</summary>
[TestClass]
public class GitAddressFormsTests
{
    [DataTestMethod]
    [DataRow("git@github.com:o/r.git", "git@github.com:o/r.git", "https://github.com/o/r.git")]
    [DataRow("https://github.com/o/r.git", "git@github.com:o/r.git", "https://github.com/o/r.git")]
    [DataRow("https://github.com/o/r", "git@github.com:o/r", "https://github.com/o/r")]
    [DataRow("ssh://git@gitlab.com/g/sub/r.git", "git@gitlab.com:g/sub/r.git", "https://gitlab.com/g/sub/r.git")]
    [DataRow("https://gitlab.com/g/sub/r.git", "git@gitlab.com:g/sub/r.git", "https://gitlab.com/g/sub/r.git")]
    public void BothFormsAreWorkedOut(string input, string ssh, string https)
    {
        var f = GitAddressForms.Analyse(input);
        Assert.AreEqual(ssh, f.Ssh);
        Assert.AreEqual(https, f.Https);
        Assert.IsFalse(f.HadCredentials);
    }

    [TestMethod]
    public void ThePasswordOrTokenInAnHttpsAddressIsRemoved_AndFlagged()
    {
        var f = GitAddressForms.Analyse("https://me:ghp_secret123@github.com/o/r.git");
        Assert.IsTrue(f.HadCredentials);
        Assert.AreEqual("https://github.com/o/r.git", f.Https);
        Assert.AreEqual("git@github.com:o/r.git", f.Ssh);
        Assert.IsFalse((f.Original + f.Https + f.Ssh).Contains("ghp_secret123"));
        Assert.IsFalse((f.Original + f.Https + f.Ssh).Contains("me:"));
    }

    [TestMethod]
    public void ATokenAsTheOnlyUserPart_IsRemovedToo()
    {
        var f = GitAddressForms.Analyse("https://ghp_abc@github.com/o/r.git");
        Assert.IsTrue(f.HadCredentials);
        Assert.AreEqual("https://github.com/o/r.git", f.Original);
    }

    [DataTestMethod]
    [DataRow("https://dev.azure.com/org/proj/_git/repo")]
    [DataRow("git@ssh.dev.azure.com:v3/org/proj/repo")]
    [DataRow("ssh://git@host:2222/o/r.git")]
    [DataRow("/srv/git/repo.git")]
    [DataRow("file:///srv/git/repo.git")]
    [DataRow("C:\\work\\repo")]
    public void AddressesWithNoSafeTranslation_AreKeptAsTheyAre_WithNoOtherForm(string input)
    {
        var f = GitAddressForms.Analyse(input);
        Assert.AreEqual(input, f.Original);
        Assert.IsNull(f.Ssh == input ? f.Https : f.Ssh, "no guessed twin for an unusual address");
    }

    [TestMethod]
    public void TheOriginalFormIsAlwaysAvailable()
    {
        var f = GitAddressForms.Analyse("git@github.com:o/r.git");
        Assert.AreEqual("git@github.com:o/r.git", f.Original);
        Assert.AreEqual("github.com/o/r", f.Key);
    }

    [TestMethod]
    public void SameRepositoryInEitherForm_GivesTheSameKey()
    {
        var a = GitAddressForms.Analyse("git@github.com:Org/Repo.git").Key;
        Assert.AreEqual(a, GitAddressForms.Analyse("https://github.com/org/repo").Key);
        Assert.AreEqual(a, GitAddressForms.Analyse("https://user:tok@GitHub.com/org/repo.git").Key);
        Assert.AreNotEqual(a, GitAddressForms.Analyse("https://github.com/org/other").Key);
    }

    [TestMethod]
    public void BlankOrJunk_IsHandledWithoutThrowing()
    {
        Assert.IsNull(GitAddressForms.Analyse("").Ssh);
        Assert.IsNull(GitAddressForms.Analyse("   ").Https);
        Assert.AreEqual("", GitAddressForms.Analyse(null).Original);
    }

    // ── hiding secrets in text ───────────────────────────────────────────────

    [DataTestMethod]
    [DataRow("fatal: unable to access 'https://me:ghp_secret@github.com/o/r.git/': 403", "fatal: unable to access 'https://***@github.com/o/r.git/': 403")]
    [DataRow("cloning https://ghp_tok@github.com/o/r.git now", "cloning https://***@github.com/o/r.git now")]
    [DataRow("ssh://git:pw@host/x and http://u:p@h/y", "ssh://***@host/x and http://***@h/y")]
    public void Redact_HidesCredentialsInsideAddresses(string input, string expected) =>
        Assert.AreEqual(expected, GitAddressForms.Redact(input));

    [DataTestMethod]
    [DataRow("git@github.com:o/r.git")]
    [DataRow("https://github.com/o/r.git")]
    [DataRow("no address here")]
    [DataRow("")]
    public void Redact_LeavesOrdinaryTextAlone(string input) =>
        Assert.AreEqual(input, GitAddressForms.Redact(input));

    [TestMethod]
    public void Redact_ToleratesNull() => Assert.AreEqual("", GitAddressForms.Redact(null));
}
