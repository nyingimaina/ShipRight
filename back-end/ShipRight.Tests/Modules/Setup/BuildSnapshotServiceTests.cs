using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Setup;

namespace ShipRight.Tests.Modules.Setup;

[TestClass]
public class BuildSnapshotServiceTests
{
    private const string Sha = "3f2a9c1d0e8b7a6f5e4d3c2b1a0f9e8d7c6b5a4f";
    private const string Home = "/home/dev";

    private static RecordingRunner Runner() =>
        new RecordingRunner().When((_, a) => a.Contains("printenv"), stdout: Home + "\n");

    private static string AllCalls(RecordingRunner r) =>
        string.Join("\n", Enumerable.Range(0, r.Calls.Count).Select(r.Joined));

    [TestMethod]
    public async Task WslHome_IsReadFromWsl()
    {
        var home = await new BuildSnapshotService(Runner()).GetWslHomeAsync();

        Assert.AreEqual(Home, home);
    }

    [TestMethod]
    public async Task WslHome_WhenWslIsUnavailable_SaysSoPlainly()
    {
        var runner = new RecordingRunner().When((_, a) => a.Contains("printenv"), exit: 1, stderr: "no distro");

        var ex = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => new BuildSnapshotService(runner).GetWslHomeAsync());

        StringAssert.Contains(ex.Message, "WSL");
    }

    [TestMethod]
    public async Task Snapshot_IsACheckoutOfTheExactCommitInsideWsl()
    {
        var runner = Runner();

        var snap = await new BuildSnapshotService(runner).CreateAsync(@"D:\work\app", Sha, "proj1", "build9");

        Assert.AreEqual($"{Home}/.shipright/build/proj1/build9", snap.WorkDir);
        Assert.AreEqual(Sha, snap.Sha);
        Assert.IsTrue(runner.Any($"worktree add --detach {Home}/.shipright/build/proj1/build9 {Sha}"));
    }

    [TestMethod]
    public async Task EveryCommandRunsThroughWsl()
    {
        var runner = Runner();

        await new BuildSnapshotService(runner).CreateAsync(@"D:\work\app", Sha, "proj1", "build9");

        Assert.IsTrue(runner.Calls.All(c => c.Exe == "wsl"));
    }

    [TestMethod]
    public async Task TheUsersRepo_IsOnlyEverReadFrom_ViaAFetchIntoAPrivateMirror()
    {
        var runner = Runner();

        await new BuildSnapshotService(runner).CreateAsync(@"D:\work\app", Sha, "proj1", "build9");

        var all = AllCalls(runner);
        StringAssert.Contains(all, $"--git-dir {Home}/.shipright/cache/proj1.git fetch");
        StringAssert.Contains(all, "/mnt/d/work/app");
        // Nothing may run git *inside* the user's repo with a mutating command.
        Assert.IsFalse(all.Contains("-C /mnt/d/work/app worktree"));
        Assert.IsFalse(all.Contains("-C /mnt/d/work/app checkout"));
    }

    [TestMethod]
    public async Task Fetch_TrustsTheWindowsOwnedRepo_AndFetchesBranchesAndHead()
    {
        var runner = Runner();

        await new BuildSnapshotService(runner).CreateAsync(@"D:\work\app", Sha, "proj1", "build9");

        var fetch = runner.Calls.Single(c => c.Args.Contains("fetch"));
        Assert.IsTrue(fetch.Args.Contains("safe.directory=*"));
        Assert.IsTrue(fetch.Args.Contains("+refs/heads/*:refs/heads/*"));
        Assert.IsTrue(fetch.Args.Contains("+HEAD:refs/shipright/head"));
    }

    [TestMethod]
    public async Task MirrorIsCreatedOnFirstUseOnly()
    {
        var first = Runner().When((_, a) => a.Contains("test"), exit: 1);
        await new BuildSnapshotService(first).CreateAsync(@"D:\work\app", Sha, "proj1", "b1");
        Assert.IsTrue(first.Any("init --bare"));

        var later = Runner().When((_, a) => a.Contains("test"), exit: 0);
        await new BuildSnapshotService(later).CreateAsync(@"D:\work\app", Sha, "proj1", "b2");
        Assert.IsFalse(later.Any("init --bare"));
    }

    [TestMethod]
    public async Task CommitMissingFromTheMirror_FailsWithAPlainExplanation()
    {
        var runner = Runner().WhenArgs("cat-file", exit: 128, stderr: "fatal: Not a valid object name");

        var ex = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            new BuildSnapshotService(runner).CreateAsync(@"D:\work\app", Sha, "proj1", "build9"));

        StringAssert.Contains(ex.Message, "commit");
        Assert.IsFalse(runner.Any("worktree add"));
    }

    [TestMethod]
    public async Task FetchFailure_SurfacesGitsMessage()
    {
        var runner = Runner().WhenArgs("fetch", exit: 128, stderr: "fatal: unable to access");

        var ex = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            new BuildSnapshotService(runner).CreateAsync(@"D:\work\app", Sha, "proj1", "build9"));

        StringAssert.Contains(ex.Message, "unable to access");
    }

    [TestMethod]
    public async Task WslNotAvailable_FailsWithAClearMessage()
    {
        var runner = new RecordingRunner().When((_, a) => a.Contains("printenv"), exit: 1, stderr: "no distro");

        var ex = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            new BuildSnapshotService(runner).CreateAsync(@"D:\work\app", Sha, "proj1", "build9"));

        StringAssert.Contains(ex.Message, "WSL");
    }

    [TestMethod]
    [DataRow("../evil", "b")]
    [DataRow("p", "../evil")]
    [DataRow("p/q", "b")]
    [DataRow("p", "b c")]
    [DataRow("", "b")]
    [DataRow("p", "")]
    public async Task UnsafeProjectOrBuildIds_AreRejectedBeforeAnythingRuns(string project, string build)
    {
        var runner = Runner();

        await Assert.ThrowsExceptionAsync<ArgumentException>(() =>
            new BuildSnapshotService(runner).CreateAsync(@"D:\work\app", Sha, project, build));

        Assert.AreEqual(0, runner.Calls.Count);
    }

    [TestMethod]
    [DataRow("not-a-sha")]
    [DataRow("")]
    [DataRow("--upload-pack=evil")]
    [DataRow("3f2a9c1")]
    public async Task OnlyFullCommitHashes_AreAccepted(string sha)
    {
        var runner = Runner();

        await Assert.ThrowsExceptionAsync<ArgumentException>(() =>
            new BuildSnapshotService(runner).CreateAsync(@"D:\work\app", sha, "proj1", "build9"));

        Assert.AreEqual(0, runner.Calls.Count);
    }

    [TestMethod]
    public async Task Remove_DeletesTheWorktreeAndPrunes()
    {
        var runner = Runner();
        var svc = new BuildSnapshotService(runner);
        var snap = await svc.CreateAsync(@"D:\work\app", Sha, "proj1", "build9");
        runner.Calls.Clear();

        await svc.RemoveAsync(snap);

        Assert.IsTrue(runner.Any($"worktree remove --force {snap.WorkDir}"));
    }

    [TestMethod]
    public async Task Remove_FallsBackToDeletingTheFolderAndPruningWhenWorktreeRemoveFails()
    {
        var runner = Runner();
        var svc = new BuildSnapshotService(runner);
        var snap = await svc.CreateAsync(@"D:\work\app", Sha, "proj1", "build9");
        runner.Calls.Clear();
        runner.WhenArgs("remove", exit: 1, stderr: "busy");

        await svc.RemoveAsync(snap);

        Assert.IsTrue(runner.Any($"rm -rf {snap.WorkDir}"));
        Assert.IsTrue(runner.Any("worktree prune"));
    }

    [TestMethod]
    public async Task Remove_NeverThrows_EvenIfWslIsGone()
    {
        var runner = Runner();
        var svc = new BuildSnapshotService(runner);
        var snap = await svc.CreateAsync(@"D:\work\app", Sha, "proj1", "build9");
        runner.When((_, _) => true, exit: 1, stderr: "wsl gone");

        await svc.RemoveAsync(snap);
    }

    [TestMethod]
    public async Task Remove_RefusesToDeleteAnythingOutsideTheBuildArea()
    {
        var runner = Runner();
        var forged = new BuildSnapshot("/home/dev/important", Sha, $"{Home}/.shipright/cache/p.git");

        await new BuildSnapshotService(runner).RemoveAsync(forged);

        Assert.AreEqual(0, runner.Calls.Count);
    }
}
