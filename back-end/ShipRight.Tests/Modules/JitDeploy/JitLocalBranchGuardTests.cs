using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.JitDeploy;
using ShipRight.Modules.Projects;
using ShipRight.Tests.Modules.Setup;

namespace ShipRight.Tests.Modules.JitDeploy;

/// <summary>
/// The server fetches origin/branch, so local work that was never committed or pushed is invisible to a deploy.
/// These tests pin the preflight that refuses that situation before any server work starts.
/// </summary>
[TestClass]
public class JitLocalBranchGuardTests
{
    private const string Repo = @"D:\work\app";
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
        string origin = Origin, string branch = "master", string status = "", string counts = "0 0", bool pushed = true)
    {
        var r = new RecordingRunner();
        r.When((_, a) => a.Contains("remote") && a.Contains("get-url"), 0, origin + "\n");
        r.When((_, a) => a.Contains("--abbrev-ref"), 0, branch + "\n");
        r.When((_, a) => a.Any(x => x.StartsWith("--porcelain", StringComparison.Ordinal)), 0, status);
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
    public async Task UnsavedChanges_AreRefusedWithAClearReason()
    {
        var guard = new JitLocalBranchGuard(Runner(status: "M  src/Program.cs\n?? new.sql\n"));

        var result = await guard.CheckAsync(Project(), Config());

        Assert.IsFalse(result.Clean);
        StringAssert.Contains(result.Problem!, "commit", StringComparison.OrdinalIgnoreCase);
        StringAssert.Contains(result.Problem!, "src/Program.cs");
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
    public async Task LocalCheckoutOnADifferentBranch_IsRefused()
    {
        var guard = new JitLocalBranchGuard(Runner(branch: "feature/x"));

        var result = await guard.CheckAsync(Project(), Config());

        StringAssert.Contains(result.Problem!, "feature/x");
        StringAssert.Contains(result.Problem!, "master");
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
}
