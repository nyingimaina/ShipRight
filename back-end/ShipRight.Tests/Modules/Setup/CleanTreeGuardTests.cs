using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Setup;

namespace ShipRight.Tests.Modules.Setup;

[TestClass]
public class CleanTreeGuardTests
{
    private const string Sha = "3f2a9c1d0e8b7a6f5e4d3c2b1a0f9e8d7c6b5a4f";

    private static RecordingRunner Runner(string status, int statusExit = 0, string head = Sha, int headExit = 0) =>
        new RecordingRunner()
            .When((_, a) => a.Contains("status"), statusExit, status, statusExit == 0 ? "" : "fatal: not a git repository")
            .When((_, a) => a.Contains("rev-parse"), headExit, head + "\n", headExit == 0 ? "" : "fatal: ambiguous argument 'HEAD'");

    [TestMethod]
    public async Task CleanRepo_IsCleanAndReportsTheHeadCommit()
    {
        var result = await new CleanTreeGuard(Runner("")).CheckAsync(@"D:\work\app");

        Assert.IsTrue(result.IsClean);
        Assert.AreEqual(Sha, result.Sha);
        Assert.AreEqual(0, result.Entries.Count);
        Assert.IsNull(result.Error);
    }

    [TestMethod]
    public async Task StatusIsQueriedInPorcelainFormatIncludingUntrackedFiles()
    {
        var runner = Runner("");

        await new CleanTreeGuard(runner).CheckAsync(@"D:\work\app");

        var status = runner.Calls.Single(c => c.Args.Contains("status"));
        Assert.IsTrue(status.Args.Contains("--porcelain=v1"));
        Assert.IsTrue(status.Args.Contains("--untracked-files=normal"));
        Assert.IsTrue(status.Args.Contains(@"D:\work\app"));
    }

    [TestMethod]
    public async Task ModifiedFile_MakesTheTreeDirty_WithAPlainEnglishDescription()
    {
        var result = await new CleanTreeGuard(Runner(" M src/Program.cs\n")).CheckAsync(@"D:\work\app");

        Assert.IsFalse(result.IsClean);
        var e = result.Entries.Single();
        Assert.AreEqual("src/Program.cs", e.Path);
        Assert.AreEqual("Changed", e.Description);
    }

    [TestMethod]
    public async Task UntrackedFile_CountsAsDirtyBecauseAForgottenNewFileWouldSilentlyMissTheBuild()
    {
        var result = await new CleanTreeGuard(Runner("?? NewService.cs\n")).CheckAsync(@"D:\work\app");

        Assert.IsFalse(result.IsClean);
        Assert.AreEqual("New file, not saved to git yet", result.Entries.Single().Description);
    }

    [TestMethod]
    [DataRow("M  a.cs", "Changed")]
    [DataRow("MM a.cs", "Changed")]
    [DataRow("A  a.cs", "New file, ready to commit")]
    [DataRow(" D a.cs", "Deleted")]
    [DataRow("D  a.cs", "Deleted")]
    [DataRow("UU a.cs", "Merge conflict")]
    [DataRow("AA a.cs", "Merge conflict")]
    public void StatusCodes_AreDescribedInPlainEnglish(string line, string expected)
    {
        var entries = CleanTreeGuard.ParsePorcelain(line + "\n");

        Assert.AreEqual(expected, entries.Single().Description);
        Assert.AreEqual("a.cs", entries.Single().Path);
    }

    [TestMethod]
    public void RenamedFile_ReportsTheNewPath()
    {
        var e = CleanTreeGuard.ParsePorcelain("R  old.cs -> new.cs\n").Single();

        Assert.AreEqual("new.cs", e.Path);
        Assert.AreEqual("Renamed", e.Description);
    }

    [TestMethod]
    public void QuotedPathsWithSpaces_AreUnquoted()
    {
        var e = CleanTreeGuard.ParsePorcelain("?? \"My File.cs\"\n").Single();

        Assert.AreEqual("My File.cs", e.Path);
    }

    [TestMethod]
    public void WindowsLineEndingsAndBlankLines_AreTolerated()
    {
        var entries = CleanTreeGuard.ParsePorcelain(" M a.cs\r\n\r\n?? b.cs\r\n");

        Assert.AreEqual(2, entries.Count);
    }

    [TestMethod]
    public async Task NotARepository_IsReportedAsAnErrorAndNeverAsClean()
    {
        var result = await new CleanTreeGuard(Runner("", statusExit: 128)).CheckAsync(@"D:\work\app");

        Assert.IsFalse(result.IsClean);
        StringAssert.Contains(result.Error!, "git repository");
    }

    [TestMethod]
    public async Task RepoWithNoCommits_IsNotBuildable()
    {
        var result = await new CleanTreeGuard(Runner("", headExit: 128)).CheckAsync(@"D:\work\app");

        Assert.IsFalse(result.IsClean);
        Assert.IsNull(result.Sha);
        StringAssert.Contains(result.Error!, "no commits");
    }

    [TestMethod]
    public async Task EmptyPath_Throws()
    {
        await Assert.ThrowsExceptionAsync<ArgumentException>(() => new CleanTreeGuard(Runner("")).CheckAsync(" "));
    }
}

[TestClass]
public class GitCommitterTests
{
    [TestMethod]
    public async Task ChosenFiles_AreStagedByExplicitPathAndCommittedWithTheMessage()
    {
        var runner = new RecordingRunner();

        var result = await new GitCommitter(runner).CommitAsync(@"D:\work\app", "Save my changes", ["src/a.cs", "b.cs"]);

        Assert.IsTrue(result.Success);
        var add = runner.Calls.Single(c => c.Args.Contains("add"));
        CollectionAssert.IsSubsetOf(new[] { "--", "src/a.cs", "b.cs" }, add.Args);
        Assert.IsFalse(add.Args.Contains("-A"));
        var commit = runner.Calls.Single(c => c.Args.Contains("commit"));
        Assert.IsTrue(commit.Args.Contains("Save my changes"));
    }

    [TestMethod]
    [DataRow(".env")]
    [DataRow("config/.env.production")]
    [DataRow("keys/server.pem")]
    [DataRow("id_rsa")]
    [DataRow("cert.pfx")]
    [DataRow("secrets/app.key")]
    public async Task LikelySecretFiles_AreRefusedAndNothingIsStaged(string path)
    {
        var runner = new RecordingRunner();

        var result = await new GitCommitter(runner).CommitAsync(@"D:\work\app", "msg", ["ok.cs", path]);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error!, path);
        Assert.AreEqual(0, runner.Calls.Count);
    }

    [TestMethod]
    public async Task EnvExample_IsNotTreatedAsASecret()
    {
        var result = await new GitCommitter(new RecordingRunner()).CommitAsync(@"D:\work\app", "msg", [".env.example"]);

        Assert.IsTrue(result.Success);
    }

    [TestMethod]
    public async Task PathsThatEscapeTheRepo_AreRefused()
    {
        var runner = new RecordingRunner();

        var result = await new GitCommitter(runner).CommitAsync(@"D:\work\app", "msg", ["../other/file.cs"]);

        Assert.IsFalse(result.Success);
        Assert.AreEqual(0, runner.Calls.Count);
    }

    [TestMethod]
    public async Task NoPathsOrBlankMessage_IsRefused()
    {
        var committer = new GitCommitter(new RecordingRunner());

        Assert.IsFalse((await committer.CommitAsync(@"D:\work\app", "msg", [])).Success);
        Assert.IsFalse((await committer.CommitAsync(@"D:\work\app", "  ", ["a.cs"])).Success);
    }

    [TestMethod]
    public async Task GitFailure_IsReturnedWithGitsOwnMessage()
    {
        var runner = new RecordingRunner().WhenArgs("commit", exit: 128, stderr: "Author identity unknown");

        var result = await new GitCommitter(runner).CommitAsync(@"D:\work\app", "msg", ["a.cs"]);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error!, "Author identity unknown");
    }
}
