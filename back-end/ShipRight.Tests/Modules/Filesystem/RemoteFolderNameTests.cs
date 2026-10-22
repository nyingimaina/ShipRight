using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Filesystem;

namespace ShipRight.Tests.Modules.Filesystem;

[TestClass]
public class RemoteFolderNameTests
{
    [TestMethod]
    [DataRow("ship-right")]
    [DataRow("my app 2")]
    [DataRow(".hidden")]
    [DataRow("a.b_c-d")]
    public void AcceptsOrdinaryFolderNames(string name) => Assert.IsNull(RemoteFolderName.Validate(name));

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow(".")]
    [DataRow("..")]
    [DataRow("a/b")]
    [DataRow("../etc")]
    [DataRow("/abs")]
    [DataRow("bad\0name")]
    public void RejectsEmptyDotsAndAnythingWithASlash(string? name) =>
        Assert.IsNotNull(RemoteFolderName.Validate(name));

    [TestMethod]
    public void RejectsOverlongNames() => Assert.IsNotNull(RemoteFolderName.Validate(new string('a', 256)));

    [TestMethod]
    public void TheMessageSaysWhatIsWrong() =>
        StringAssert.Contains(RemoteFolderName.Validate("a/b")!, "/");

    [TestMethod]
    [DataRow("/home/ubuntu", "app", "/home/ubuntu/app")]
    [DataRow("/home/ubuntu/", "app", "/home/ubuntu/app")]
    [DataRow("/", "srv", "/srv")]
    [DataRow("/home/ubuntu", "  app  ", "/home/ubuntu/app")]
    public void JoinsParentAndTrimmedName(string parent, string name, string expected) =>
        Assert.AreEqual(expected, RemoteFolderName.Join(parent, name));
}
