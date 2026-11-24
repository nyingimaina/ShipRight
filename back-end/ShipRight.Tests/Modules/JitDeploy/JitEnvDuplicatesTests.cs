using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.JitDeploy;

namespace ShipRight.Tests.Modules.JitDeploy;

/// <summary>
/// When the server and the project both define the same variable, the deploy-time warning must be able to show both
/// values, and removing the duplicates from either side must leave every other line (including multiline values) alone.
/// </summary>
[TestClass]
public class JitEnvDuplicatesTests
{
    [TestMethod]
    public void Detect_FindsSharedNames_WithBothValues()
    {
        var found = JitEnvDuplicates.Detect(
            "DB=server-db\nKEEP=1\nLOG=info\n",
            "LOG=debug\nDB=project-db\nNEW=x\n");

        Assert.AreEqual(2, found.Count);
        CollectionAssert.AreEquivalent(new[] { "DB", "LOG" }, found.Select(d => d.Name).ToArray());
        Assert.AreEqual("project-db", found.Single(d => d.Name == "DB").ProjectValue);
        Assert.AreEqual("server-db", found.Single(d => d.Name == "DB").ServerValue);
        Assert.AreEqual("debug", found.Single(d => d.Name == "LOG").ProjectValue);
        Assert.AreEqual("info", found.Single(d => d.Name == "LOG").ServerValue);
    }

    [TestMethod]
    public void Detect_TreatsExportAndSpacedNamesAsTheSameVariable_LikeTheMergeDoes()
    {
        var found = JitEnvDuplicates.Detect("export DB = server-value\n", "  DB = project-value\n");

        var only = found.Single();
        Assert.AreEqual("DB", only.Name);
        Assert.AreEqual("server-value", only.ServerValue);
        Assert.AreEqual("project-value", only.ProjectValue);
    }

    [TestMethod]
    public void Detect_DoesNotMistakeALineInsideAMultilineValueForAVariable()
    {
        var server = "CERT=\"-----BEGIN\nLOG=fake-inside-the-value\n-----END\"\nKEEP=1";

        var found = JitEnvDuplicates.Detect(server, "LOG=real\nCERT=short");

        var only = found.Single();
        Assert.AreEqual("CERT", only.Name);
        StringAssert.Contains(only.ServerValue, "-----BEGIN");
        StringAssert.Contains(only.ServerValue, "LOG=fake-inside-the-value");
        Assert.AreEqual("short", only.ProjectValue);
    }

    [TestMethod]
    public void Detect_WhenNothingIsShared_ReturnsEmpty()
    {
        Assert.AreEqual(0, JitEnvDuplicates.Detect("A=1\nB=2", "C=3").Count);
        Assert.AreEqual(0, JitEnvDuplicates.Detect(null, null).Count);
        Assert.AreEqual(0, JitEnvDuplicates.Detect("# just a comment\n", "C=3").Count);
    }

    [TestMethod]
    public void Detect_UsesTheLastAssignmentOnEachSide_AsComposeDoes()
    {
        var found = JitEnvDuplicates.Detect("A=old\nA=new", "A=first\nA=last");

        var only = found.Single();
        Assert.AreEqual("new", only.ServerValue);
        Assert.AreEqual("last", only.ProjectValue);
    }

    [TestMethod]
    public void Remove_TakesOutOnlyTheNamedVariables_KeepingCommentsAndMultilineValuesIntact()
    {
        var server = "# note\nCERT=\"-----BEGIN\nLOG=fake-inside-the-value\n-----END\"\nA=1\nKEEP=2\n";

        var left = JitEnvDuplicates.Remove(server, new[] { "A" });

        StringAssert.Contains(left, "# note\n");
        StringAssert.Contains(left, "CERT=\"-----BEGIN\nLOG=fake-inside-the-value\n-----END\"\n");
        StringAssert.Contains(left, "KEEP=2\n");
        Assert.IsFalse(left.Split('\n').Any(l => l.TrimStart().StartsWith("A=")));
    }

    [TestMethod]
    public void Remove_HandlesExportAndSpacedNames_AndIsCaseSensitive()
    {
        var text = "export A = 1\nkeep=B\n  C=3\nA_2=4\n";

        var left = JitEnvDuplicates.Remove(text, new[] { "A", "c" });

        Assert.IsFalse(left.Contains("export A = 1"));
        StringAssert.Contains(left, "keep=B\n");
        StringAssert.Contains(left, "  C=3\n");
        StringAssert.Contains(left, "A_2=4\n");
    }

    [TestMethod]
    public void RemovingFromEitherSide_LeavesNoDuplicates_WithoutChangingTheOtherSide()
    {
        const string server = "DB=server-db\nKEEP=1\nLOG=info";
        const string project = "LOG=debug\nNEW=x";

        var withoutServer = JitEnvDuplicates.Remove(server, JitEnvDuplicates.Detect(server, project).Select(d => d.Name));
        var withoutProject = JitEnvDuplicates.Remove(project, JitEnvDuplicates.Detect(server, project).Select(d => d.Name));

        Assert.AreEqual(0, JitEnvDuplicates.Detect(withoutServer, project).Count);
        Assert.AreEqual(0, JitEnvDuplicates.Detect(server, withoutProject).Count);
        Assert.AreEqual("NEW=x", withoutProject);
        Assert.AreEqual("DB=server-db\nKEEP=1", withoutServer);
    }
}
