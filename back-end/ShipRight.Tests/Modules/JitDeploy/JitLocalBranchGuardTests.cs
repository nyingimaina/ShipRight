using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.JitDeploy;
using ShipRight.Modules.Projects;
using ShipRight.Tests.Modules.Setup;

namespace ShipRight.Tests.Modules.JitDeploy;

/// <summary>
/// The server fetches origin/branch, so local work that was never committed or pushed is invisible to a deploy.
/// These tests pin the preflight that refuses that situation before any server work starts, and the false positives
/// it must not report: another checked-out branch, a detached HEAD, or untracked files that no commit could contain.
/// </summary>
[TestClass]
public class JitLocalBranchGuardTests
{
    private const string Repo = @"D:\work\app";
    private const string Repo2 = @"D:\work\app-old";
    private const string Origin = "git@github.com:o/r.git";
    private const string Sha = "3f2a9c1d0e8b7a6f5e4d3c2b1a0f9e8d7c6b5a4f";

    private static ProjectConfig Project(string repoPath = Repo, string deployBranch = "master") => new()
    {
        Id = "p1",
        Name = "App",
        GitRepos = [new GitConfig { RepoPath = repoPath, DeployBranch = deployBranch }],
    };

    private static JitDeployConfig Config(string repoUrl = Origin, string branch = "master") => new()
    {
        Enabled = true,
        RepoUrl = repoUrl,
        Branch = branch,
    };

    private static RecordingRunner Runner(
        string origin = Origin, string branch = "master", string status = "", string counts = "0 0", bool pushed = true,
        bool fetchOk = true, bool lineEndingsOnly = false)
    {
        var r = new RecordingRunner();
        r.When((_, a) => a.Contains("remote") && a.Contains("get-url"), 0, origin + "\n");
        r.When((_, a) => a.Contains("fetch"), fetchOk ? 0 : 1, "", fetchOk ? "" : "fatal: could not read from remote");
        r.When((_, a) => a.Contains("--abbrev-ref"), 0, branch + "\n");
        r.When((_, a) => a.Any(x => x.StartsWith("--porcelain", StringComparison.Ordinal)), 0, status);
        r.When((_, a) => a.Contains("--ignore-cr-at-eol"), lineEndingsOnly ? 0 : 1, "");
        r.When((_, a) => a.Contains("--verify"), pushed ? 0 : 1, pushed ? Sha + "\n" : "", pushed ? "" : "fatal: no such ref");
        r.When((_, a) => a.Contains("rev-list"), 0, counts + "\n");
        return r;
    }

    [TestMethod]
    public async Task CleanFullyPushedBranch_IsAllowed()
    {
        var result = await new JitLocalBranchGuard(Runner()).CheckAsync(Project(), Config());

        Assert.IsTrue(result.Applicable);
        Assert.IsTrue(result.Clean);
        Assert.AreEqual(0, result.Ahead);
        Assert.IsNull(result.Problem);
    }

    [TestMethod]
    public async Task TrackedChangesOnTheDeployBranch_AreRefusedWithAClearReason()
    {
        var guard = new JitLocalBranchGuard(Runner(status: "M  src/Program.cs\n"));

        var result = await guard.CheckAsync(Project(), Config());

        Assert.IsFalse(result.Clean);
        StringAssert.Contains(result.Problem!, "commit", StringComparison.OrdinalIgnoreCase);
        StringAssert.Contains(result.Problem!, "src/Program.cs");
        StringAssert.Contains(result.Problem!, Sha[..7]);
        StringAssert.Contains(result.Problem!, "Deploy this exact commit");
    }

    [TestMethod]
    public async Task UntrackedFilesOnly_AreAllowedButNamedAsAWarning()
    {
        var guard = new JitLocalBranchGuard(Runner(status: "?? scratch.sql\n?? notes/todo.md\n"));

        var result = await guard.CheckAsync(Project(), Config());

        Assert.IsTrue(result.Clean);
        Assert.IsNull(result.Problem);
        var warning = result.Warnings.Single(w => w.Contains("Untracked", StringComparison.Ordinal));
        StringAssert.Contains(warning, "scratch.sql");
        StringAssert.Contains(warning, "notes/todo.md");
        StringAssert.Contains(warning, "will not be on the server");
    }

    [TestMethod]
    public async Task TrackedChangesThatAreOnlyLineEndings_DoNotBlock()
    {
        var guard = new JitLocalBranchGuard(Runner(status: "M  src/Program.cs\nM  README.md\n", lineEndingsOnly: true));

        var result = await guard.CheckAsync(Project(), Config());

        Assert.IsNull(result.Problem, "line-ending churn is not work the deploy would leave out");
        Assert.IsTrue(result.Warnings.Any(w => w.Contains("only in line endings")),
            "the person is told why those files are not real changes");
    }

    [TestMethod]
    public async Task CommitsThatWereNeverPushed_AreRefusedWithTheCount()
    {
        var guard = new JitLocalBranchGuard(Runner(counts: "0 2"));

        var result = await guard.CheckAsync(Project(), Config());

        Assert.AreEqual(2, result.Ahead);
        StringAssert.Contains(result.Problem!, "push", StringComparison.OrdinalIgnoreCase);
        StringAssert.Contains(result.Problem!, "2");
    }

    [TestMethod]
    public async Task BranchThatHasNeverBeenPushed_IsRefused()
    {
        var guard = new JitLocalBranchGuard(Runner(pushed: false));

        var result = await guard.CheckAsync(Project(), Config());

        StringAssert.Contains(result.Problem!, "pushed", StringComparison.OrdinalIgnoreCase);
        StringAssert.Contains(result.Problem!, "master");
    }

    [TestMethod]
    public async Task LocalCheckoutOnADifferentBranch_IsAllowedWhenTheDeployBranchIsPushed()
    {
        var guard = new JitLocalBranchGuard(Runner(branch: "feature/x"));

        var result = await guard.CheckAsync(Project(), Config());

        Assert.IsNull(result.Problem);
        Assert.IsTrue(result.Warnings.Any(w => w.Contains("feature/x") && w.Contains("master")),
            "the person is told which branch was actually checked, not the one on disk");
    }

    [TestMethod]
    public async Task DetachedHead_IsAllowedWhenTheDeployBranchIsPushed()
    {
        var guard = new JitLocalBranchGuard(Runner(branch: "HEAD"));

        var result = await guard.CheckAsync(Project(), Config());

        Assert.IsNull(result.Problem);
        Assert.IsTrue(result.Warnings.Any(w => w.Contains("HEAD")));
    }

    [TestMethod]
    public async Task TrackedChangesOnAnotherBranch_AreOnlyAWarning()
    {
        var guard = new JitLocalBranchGuard(Runner(branch: "feature/x", status: "M  src/Program.cs\n"));

        var result = await guard.CheckAsync(Project(), Config());

        Assert.IsNull(result.Problem);
        Assert.IsTrue(result.Warnings.Any(w => w.Contains("src/Program.cs") && w.Contains("not part of 'master'")));
    }

    [TestMethod]
    public async Task StaleRemoteIsRefreshedBeforeComparing()
    {
        var runner = Runner(counts: "0 0");
        await new JitLocalBranchGuard(runner).CheckAsync(Project(), Config());

        var fetch = runner.Calls.FindIndex(c => c.Args.Contains("fetch"));
        var compare = runner.Calls.FindIndex(c => c.Args.Contains("rev-list"));
        Assert.IsTrue(fetch >= 0, "the remote branch must be refreshed");
        Assert.IsTrue(compare > fetch, "the comparison must use the refreshed remote ref");
        Assert.IsTrue(runner.Calls[fetch].Args.Any(a => a.Contains("+refs/heads/master:refs/remotes/origin/master")));
    }

    [TestMethod]
    public async Task FetchFailure_UsesTheLastFetchedRemoteRefWithAWarning()
    {
        var guard = new JitLocalBranchGuard(Runner(fetchOk: false));

        var result = await guard.CheckAsync(Project(), Config());

        Assert.IsNull(result.Problem);
        Assert.IsTrue(result.Warnings.Any(w => w.Contains("Could not refresh origin/master")));
    }

    [TestMethod]
    public async Task UnpushedCommitsWithAFailedRefresh_SayTheCheckCouldNotSeeOrigin()
    {
        var guard = new JitLocalBranchGuard(Runner(counts: "0 1", fetchOk: false));

        var result = await guard.CheckAsync(Project(), Config());

        StringAssert.Contains(result.Problem!, "could not refresh");
        StringAssert.Contains(result.Problem!, "git fetch");
        StringAssert.Contains(result.Problem!, "already on origin");
    }

    [TestMethod]
    public async Task LocalBranchMissingButRemoteExists_IsAllowedWithAWarning()
    {
        var runner = new RecordingRunner()
            .When((_, a) => a.Contains("remote") && a.Contains("get-url"), 0, Origin + "\n")
            .When((_, a) => a.Contains("fetch"), 0, "")
            .When((_, a) => a.Contains("--abbrev-ref"), 0, "HEAD\n")
            .When((_, a) => a.Any(x => x.StartsWith("--porcelain", StringComparison.Ordinal)), 0, "")
            .When((_, a) => a.Contains("--verify") && a.Any(x => x.Contains("refs/heads/master")), 1, "")
            .When((_, a) => a.Contains("--verify") && a.Any(x => x.Contains("origin/master")), 0, Sha + "\n");

        var result = await new JitLocalBranchGuard(runner).CheckAsync(Project(), Config());

        Assert.IsNull(result.Problem);
        Assert.IsTrue(result.Warnings.Any(w => w.Contains("no local branch 'master'")));
    }

    [TestMethod]
    public async Task EveryMatchingCheckoutIsChecked_NotOnlyTheFirst()
    {
        var runner = new RecordingRunner()
            .When((_, a) => a.Contains("remote") && a.Contains("get-url"), 0, Origin + "\n")
            .When((_, a) => a.Contains("fetch"), 0, "")
            .When((_, a) => a.Contains("--abbrev-ref"), 0, "master\n")
            .When((_, a) => a.Any(x => x.StartsWith("--porcelain", StringComparison.Ordinal)) && a.Contains(Repo2), 0, "M  old.cs\n")
            .When((_, a) => a.Any(x => x.StartsWith("--porcelain", StringComparison.Ordinal)), 0, "")
            .When((_, a) => a.Contains("--ignore-cr-at-eol"), 1, "")
            .When((_, a) => a.Contains("--verify"), 0, Sha + "\n")
            .When((_, a) => a.Contains("rev-list"), 0, "0 0\n");

        var project = new ProjectConfig
        {
            Id = "p1", Name = "App",
            GitRepos =
            [
                new GitConfig { RepoPath = Repo, DeployBranch = "master" },
                new GitConfig { RepoPath = Repo2, DeployBranch = "master" },
            ],
        };

        var result = await new JitLocalBranchGuard(runner).CheckAsync(project, Config());

        Assert.AreEqual(2, result.Report!.Repos.Count);
        Assert.IsNotNull(result.Problem);
        StringAssert.Contains(result.Problem!, "old.cs");
        Assert.AreEqual(Repo2, result.RepoPath, "the blocking checkout is the one reported");
    }

    [TestMethod]
    public async Task NoLocalRepoMatchingTheDeployUrl_IsSkipped()
    {
        var guard = new JitLocalBranchGuard(Runner(origin: "git@github.com:someone/else.git"));

        var result = await guard.CheckAsync(Project(), Config());

        Assert.IsFalse(result.Applicable);
        Assert.IsNull(result.Problem);
    }

    [TestMethod]
    public async Task UnusableRepoPath_IsSkippedWithoutRunningGit()
    {
        var runner = new RecordingRunner();

        var result = await new JitLocalBranchGuard(runner).CheckAsync(Project("   "), Config());

        Assert.IsFalse(result.Applicable);
        Assert.IsNull(result.Problem);
        Assert.AreEqual(0, runner.Calls.Count);
    }

    [TestMethod]
    public async Task GitThatCannotBeRead_DoesNotCrashThePreflight()
    {
        var runner = new RecordingRunner();
        runner.When((_, _) => true, 1, "", "fatal: not a git repository");

        var result = await new JitLocalBranchGuard(runner).CheckAsync(Project(), Config());

        Assert.IsFalse(result.Applicable);
        Assert.IsNull(result.Problem);
        Assert.IsTrue(result.Warnings.Any(w => w.Contains("Could not read the git address")));
    }
}
