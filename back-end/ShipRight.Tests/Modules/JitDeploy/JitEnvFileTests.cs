using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.JitDeploy;

namespace ShipRight.Tests.Modules.JitDeploy;

/// <summary>
/// The variables a build-on-server deploy hands to compose: the server's settings and the project's own are merged by
/// variable name, the project's winning, into one file that lives beside the deploy state (never in the git checkout).
/// </summary>
[TestClass]
public class JitEnvFileTests
{
    private static readonly JitTarget Target = new("h", "u", "/k", "/home/deploy/shop");

    private static Dictionary<string, string> Parse(string text) =>
        text.Split('\n').Where(l => l.Contains('=') && !l.TrimStart().StartsWith('#'))
            .Select(l => l.Split('=', 2)).GroupBy(p => p[0]).ToDictionary(g => g.Key, g => g.Last()[1]);

    // ── merge ────────────────────────────────────────────────────────────────

    [TestMethod]
    public void NothingFromEitherSide_MeansNoFile()
    {
        Assert.IsNull(JitEnvFile.Merge(null, null));
        Assert.IsNull(JitEnvFile.Merge("", "  \n\n"));
        Assert.IsNull(JitEnvFile.Merge("# only a comment\n", null));
    }

    [TestMethod]
    public void OnlyServerVariables_AreKeptAsTheyAre()
    {
        var merged = JitEnvFile.Merge("A=1\nB=two words\n", null)!;
        var vars = Parse(merged);
        Assert.AreEqual("1", vars["A"]);
        Assert.AreEqual("two words", vars["B"]);
        Assert.IsTrue(merged.EndsWith('\n'));
    }

    [TestMethod]
    public void OnlyProjectVariables_AreKept()
    {
        var vars = Parse(JitEnvFile.Merge(null, "X=9")!);
        Assert.AreEqual("9", vars["X"]);
    }

    [TestMethod]
    public void ProjectWinsPerVariable_AndTheRestOfTheServersVariablesSurvive()
    {
        var merged = JitEnvFile.Merge("DB=server-db\nLOG=info\nKEY=k", "LOG=debug\nEXTRA=1")!;
        var lines = merged.Split('\n').Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
        CollectionAssert.AreEquivalent(new[] { "DB=server-db", "KEY=k", "LOG=debug", "EXTRA=1" }, lines);
        Assert.AreEqual(1, lines.Count(l => l.StartsWith("LOG=")), "the overridden server line is gone, not just shadowed");
    }

    [TestMethod]
    public void ValuesAreNeverInterpreted_DollarsQuotesAndEqualsSignsComeThroughUntouched()
    {
        var merged = JitEnvFile.Merge("P=ab$mK2=x\nQ=\"quoted # not a comment\"", "R='single $kept'")!;
        StringAssert.Contains(merged, "P=ab$mK2=x\n");
        StringAssert.Contains(merged, "Q=\"quoted # not a comment\"\n");
        StringAssert.Contains(merged, "R='single $kept'\n");
    }

    [TestMethod]
    public void ExportPrefixAndSpacesAroundTheName_CountAsTheSameVariable()
    {
        var merged = JitEnvFile.Merge("export LOG=info\n  DB = x", "LOG=debug\nDB=y")!;
        Assert.AreEqual(1, merged.Split('\n').Count(l => l.Contains("LOG=")));
        Assert.AreEqual(1, merged.Split('\n').Count(l => l.Replace(" ", "").Contains("DB=")));
        StringAssert.Contains(merged, "LOG=debug");
        StringAssert.Contains(merged, "DB=y");
    }

    [TestMethod]
    public void WindowsLineEndings_AreNormalised()
    {
        var merged = JitEnvFile.Merge("A=1\r\nB=2\r\n", "C=3\r\n")!;
        Assert.IsFalse(merged.Contains('\r'));
        Assert.AreEqual("3", Parse(merged)["C"]);
    }

    [TestMethod]
    public void AMultiLineQuotedValue_IsOverriddenAsOneUnit_NotLeftInPieces()
    {
        var server = "CERT=\"-----BEGIN\nLOG=fake-inside-the-value\n-----END\"\nKEEP=1";
        var merged = JitEnvFile.Merge(server, "CERT=short")!;
        StringAssert.Contains(merged, "CERT=short");
        Assert.IsFalse(merged.Contains("BEGIN"), "no fragment of the replaced value stays behind");
        Assert.IsFalse(merged.Contains("fake-inside-the-value"));
        StringAssert.Contains(merged, "KEEP=1");
    }

    [TestMethod]
    public void AMultiLineValueThatIsNotOverridden_IsKeptIntact_AndItsInnerLinesAreNotTreatedAsVariables()
    {
        var server = "CERT=\"-----BEGIN\nLOG=inside\n-----END\"";
        var merged = JitEnvFile.Merge(server, "LOG=real")!;
        StringAssert.Contains(merged, "CERT=\"-----BEGIN\nLOG=inside\n-----END\"");
        StringAssert.Contains(merged, "LOG=real");
    }

    [TestMethod]
    public void TheProjectsLaterDuplicateInItsOwnText_StillWinsInsideTheFile()
    {
        var vars = Parse(JitEnvFile.Merge(null, "A=1\nA=2")!);
        Assert.AreEqual("2", vars["A"]);
    }

    // ── summary: names only, never values ────────────────────────────────────

    [TestMethod]
    public void Summary_CountsEachSide_AndNamesTheOverriddenVariables()
    {
        var s = JitEnvFile.Summarise("DB=a\nLOG=info\nKEY=secret", "LOG=debug\nEXTRA=1");
        Assert.AreEqual(2, s.FromServer);
        Assert.AreEqual(2, s.FromProject);
        CollectionAssert.AreEqual(new[] { "LOG" }, s.Overridden.ToArray());
    }

    [TestMethod]
    public void Summary_Describe_NeverContainsAValue()
    {
        var note = JitEnvFile.Describe(JitEnvFile.Summarise("KEY=topsecret\nLOG=info", "LOG=hunter2"));
        StringAssert.Contains(note, "LOG");
        Assert.IsFalse(note.Contains("topsecret") || note.Contains("hunter2") || note.Contains("info"));
    }

    [TestMethod]
    public void KeysOf_ListsTheNames_InOrder_SkippingCommentsAndBlankLines()
    {
        CollectionAssert.AreEqual(new[] { "A", "B_2" }, JitEnvFile.KeysOf("# c\n\nA=1\nexport B_2=x\n").ToArray());
    }

    // ── validation ───────────────────────────────────────────────────────────

    [TestMethod]
    public void Problem_IsNullForOrdinaryText_AndForNothing()
    {
        Assert.IsNull(JitEnvFile.Problem(null));
        Assert.IsNull(JitEnvFile.Problem("A=1\nB=\"x y\"\n# note\n"));
    }

    [TestMethod]
    public void Problem_RefusesControlCharacters_AndHugeText()
    {
        StringAssert.Contains(JitEnvFile.Problem("A=1\u0000")!, "character");
        StringAssert.Contains(JitEnvFile.Problem("A=" + new string('x', 70_000))!, "too large");
    }

    // ── writing to the server ────────────────────────────────────────────────

    [TestMethod]
    public void Path_IsBesideTheDeployState_NotInTheCheckout()
    {
        Assert.AreEqual("/home/deploy/shop/.deploy-state/deploy.env", JitEnvFile.Path(Target));
        Assert.IsFalse(JitEnvFile.Path(Target).StartsWith(Target.RepoDir));
    }

    [TestMethod]
    public void WriteCommand_IsPrivate_Atomic_AndKeepsTheValuesOutOfTheCommandText()
    {
        var cmd = JitEnvFile.WriteCommand(Target, "DB_PASS=hunter2\n");
        StringAssert.Contains(cmd, "umask 077");
        StringAssert.Contains(cmd, "chmod 600");
        StringAssert.Contains(cmd, "base64 -d");
        StringAssert.Contains(cmd, ".deploy-state/deploy.env.tmp");
        StringAssert.Contains(cmd, "mv -f");
        Assert.IsFalse(cmd.Contains("hunter2"), "values travel encoded, never readable in the command or a process list");
        Assert.IsFalse(cmd.Contains("ShipRight", StringComparison.OrdinalIgnoreCase));
        var b64 = System.Text.RegularExpressions.Regex.Match(cmd, @"echo '([A-Za-z0-9+/=]+)'").Groups[1].Value;
        Assert.AreEqual("DB_PASS=hunter2\n", Encoding.UTF8.GetString(Convert.FromBase64String(b64)));
    }

    [TestMethod]
    public void WriteCommand_RefusesAnUnsafeFolder()
    {
        Assert.ThrowsException<ArgumentException>(() => JitEnvFile.WriteCommand(new JitTarget("h", "u", "/k", "relative/dir"), "A=1"));
    }
}
