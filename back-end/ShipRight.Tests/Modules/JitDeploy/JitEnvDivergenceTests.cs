using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.JitDeploy;

namespace ShipRight.Tests.Modules.JitDeploy;

/// <summary>
/// The server settings saved on the server record and the copy inside the project can drift apart. Only the project copy is
/// merged into a deploy, so a deploy-time warning has to show both sides, values included.
/// </summary>
[TestClass]
public class JitEnvDivergenceTests
{
    [TestMethod]
    public void IdenticalText_IsNotADivergence()
    {
        var d = JitEnvDivergence.Detect("A=1\nB=2\n", "A=1\nB=2\n");

        Assert.IsFalse(d.Differs);
        Assert.AreEqual(0, d.Variables.Count);
    }

    [TestMethod]
    public void BothEmpty_IsNotADivergence()
    {
        var d = JitEnvDivergence.Detect(null, "");

        Assert.IsFalse(d.Differs);
    }

    [TestMethod]
    public void LineEndingsAlone_AreNotADivergence()
    {
        var d = JitEnvDivergence.Detect("A=1\r\nB=2\r\n", "A=1\nB=2\n");

        Assert.IsFalse(d.Differs);
    }

    [TestMethod]
    public void AnEmptyProjectCopy_ShowsEveryServerVariableWithAnEmptyProjectValue()
    {
        var d = JitEnvDivergence.Detect("", "A=1\nB=two\n");

        Assert.IsTrue(d.Differs);
        Assert.AreEqual(2, d.Variables.Count);
        Assert.AreEqual("A", d.Variables[0].Name);
        Assert.AreEqual("1", d.Variables[0].ServerRecordValue);
        Assert.AreEqual("", d.Variables[0].ProjectValue);
        Assert.AreEqual("B", d.Variables[1].Name);
        Assert.AreEqual("two", d.Variables[1].ServerRecordValue);
    }

    [TestMethod]
    public void DifferentValues_ShowBothValues()
    {
        var d = JitEnvDivergence.Detect("A=old\nB=same\n", "A=new\nB=same\n");

        Assert.IsTrue(d.Differs);
        Assert.AreEqual(1, d.Variables.Count);
        Assert.AreEqual("A", d.Variables[0].Name);
        Assert.AreEqual("new", d.Variables[0].ServerRecordValue);
        Assert.AreEqual("old", d.Variables[0].ProjectValue);
    }

    [TestMethod]
    public void VariablesPresentOnOnlyOneSide_AreStillShown()
    {
        var d = JitEnvDivergence.Detect("ONLY_PROJECT=1\nBOTH=x\n", "ONLY_SERVER=2\nBOTH=x\n");

        Assert.AreEqual(2, d.Variables.Count);
        var onlyProject = d.Variables.Single(v => v.Name == "ONLY_PROJECT");
        Assert.AreEqual("", onlyProject.ServerRecordValue);
        Assert.AreEqual("1", onlyProject.ProjectValue);
        var onlyServer = d.Variables.Single(v => v.Name == "ONLY_SERVER");
        Assert.AreEqual("2", onlyServer.ServerRecordValue);
        Assert.AreEqual("", onlyServer.ProjectValue);
    }

    [TestMethod]
    public void MultilineQuotedValues_AreOneVariableWithTheWholeValue()
    {
        var d = JitEnvDivergence.Detect("CERT=\"line one\nSECRET=inside\n\"\n", "CERT=\"line two\n\"\n");

        Assert.IsTrue(d.Differs);
        Assert.AreEqual(1, d.Variables.Count);
        Assert.AreEqual("CERT", d.Variables[0].Name);
        StringAssert.Contains(d.Variables[0].ServerRecordValue, "line two");
        StringAssert.Contains(d.Variables[0].ProjectValue, "line one\nSECRET=inside");
        Assert.IsFalse(d.Variables.Any(v => v.Name == "SECRET"));
    }

    [TestMethod]
    public void CommentOnlyChanges_AreStillReportedWithTheFullText()
    {
        var d = JitEnvDivergence.Detect("A=1\n# project note\n", "A=1\n# server note\n");

        Assert.IsTrue(d.Differs);
        Assert.AreEqual(0, d.Variables.Count);
        StringAssert.Contains(d.ProjectText, "project note");
        StringAssert.Contains(d.ServerRecordText, "server note");
    }
}
