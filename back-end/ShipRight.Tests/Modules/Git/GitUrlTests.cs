using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Git;

namespace ShipRight.Tests.Modules.Git;

[TestClass]
public class GitUrlTests
{
    [TestMethod]
    [DataRow("https://github.com/acme/shop")]
    [DataRow("https://github.com/acme/shop.git")]
    [DataRow("git@github.com:acme/shop.git")]
    [DataRow("ssh://git@github.com/acme/shop.git")]
    public void ParsesTheUsualForms(string raw)
    {
        var url = GitUrl.Parse(raw);

        Assert.IsNotNull(url);
        Assert.AreEqual("github", url!.Provider);
        Assert.AreEqual("acme", url.Owner);
        Assert.AreEqual("shop", url.Name);
        Assert.AreEqual("https://github.com/acme/shop", url.HttpsUrl);
        Assert.AreEqual("git@github.com:acme/shop.git", url.SshUrl);
    }

    [TestMethod]
    public void RefusesAnAddressThatCarriesASecret()
    {
        Assert.IsNull(GitUrl.Parse("https://acme:ghp_secret@github.com/acme/shop.git"));
    }

    [TestMethod]
    public void MarksAnotherHostAsGeneric()
    {
        var url = GitUrl.Parse("https://gitlab.com/acme/shop.git");

        Assert.IsNotNull(url);
        Assert.AreEqual("generic", url!.Provider);
        Assert.AreEqual("shop", url.Name);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("not a url")]
    [DataRow("https://github.com/acme")]
    public void RefusesWhatItCannotRead(string raw)
    {
        Assert.IsNull(GitUrl.Parse(raw));
    }
}
