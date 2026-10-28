using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Builds;
using ShipRight.Modules.Projects;
using ShipRight.Shared.Events;
using ShipRight.Tests.Modules.Setup;

namespace ShipRight.Tests.Modules.Builds;

[TestClass]
public class SnapshotBuildOrchestratorTests
{
    private const string Sha = "3f2a9c1d0e8b7a6f5e4d3c2b1a0f9e8d7c6b5a4f";
    private const string Sha2 = "9d8c7b6a5f4e3d2c1b0a99887766554433221100";
    private const string Home = "/home/dev";

    private sealed class Testable : SnapshotBuildOrchestrator
    {
        public Testable(RecordingRunner runner)
            : base(null!, null!, new BuildEventBus(), runner, null!, null!, null!, null!, null!, null!) { }

        public Task Build(BuildRecord record, ProjectConfig project) =>
            RunDockerBuildAsync(new PipelineContext(record, new BuildEventBus()), record, project, () => Task.CompletedTask);
    }

    /// <summary>A runner that answers like a healthy machine with a clean repo at <paramref name="sha"/>.</summary>
    private static RecordingRunner HealthyRunner(string sha = Sha) =>
        new RecordingRunner()
            .When((e, a) => e == "git" && a.Contains("status"), stdout: "")
            .When((e, a) => e == "git" && a.Contains("rev-parse"), stdout: sha + "\n")
            .When((e, a) => e == "wsl" && a.Contains("printenv"), stdout: Home + "\n")
            .When((e, a) => e == "docker" && a.Contains("inspect"), exit: 1);   // image not built yet

    private static ProjectConfig Project(BuildSource? source, params (string Name, string Context)[] services) => new()
    {
        Id = "p1",
        Name = "Shop",
        BuildSource = source,
        Services = services.Select(s => new ServiceConfig
        {
            Name = s.Name, VersionFilePath = s.Context + "\\version.txt", BuildContextPath = s.Context, DockerImageName = "shop/" + s.Name.ToLower(),
        }).ToList(),
        GitRepos = [new GitConfig { RepoPath = @"D:\work\shop", DeployBranch = "main" }],
    };

    private static BuildRecord Record(params string[] services) => new()
    {
        Id = "build9",
        ProjectId = "p1",
        Versions = services.Select(s => new ServiceVersion { ServiceName = s, NewVersion = "1.0.1", DockerImageName = "shop/" + s.ToLower() }).ToList(),
    };

    private static string[]? DockerBuildArgs(RecordingRunner r, int nth = 0) =>
        r.Calls.Where(c => c.Exe == "docker" && c.Args.Length > 0 && c.Args[0] == "build").Skip(nth).Select(c => c.Args).FirstOrDefault();

    // ── Opt-out: today's behaviour is untouched ──────────────────────────────

    [TestMethod]
    [DataRow(null)]
    [DataRow(BuildSource.InPlace)]
    public async Task ProjectsNotOptedIn_BuildFromTheirFolderExactlyAsBefore(BuildSource? source)
    {
        var runner = HealthyRunner();
        var project = Project(source, ("Api", @"D:\work\shop\Api"));

        await new Testable(runner).Build(Record("Api"), project);

        Assert.AreEqual(@"D:\work\shop\Api", DockerBuildArgs(runner)![^1]);
        Assert.IsFalse(runner.Calls.Any(c => c.Exe == "wsl"), "no snapshot machinery may run");
        Assert.IsFalse(runner.Any("status"), "no extra git checks may run");
    }

    // ── Opt-in: build from a clean copy of the committed version ─────────────

    [TestMethod]
    public async Task OptedInProjects_BuildFromACheckoutOfTheExactCommit()
    {
        var runner = HealthyRunner();
        var project = Project(BuildSource.CleanSnapshot, ("Api", @"D:\work\shop\Api"));

        await new Testable(runner).Build(Record("Api"), project);

        Assert.AreEqual($"{Home}/.shipright/build/p1/build9/Api", DockerBuildArgs(runner)![^1]);
        Assert.IsTrue(runner.Any($"worktree add --detach {Home}/.shipright/build/p1/build9 {Sha}"));
    }

    [TestMethod]
    public async Task ABuildContextAtTheRepoRoot_BuildsFromTheCheckoutRoot()
    {
        var runner = HealthyRunner();
        var project = Project(BuildSource.CleanSnapshot, ("Api", @"D:\work\shop"));

        await new Testable(runner).Build(Record("Api"), project);

        Assert.AreEqual($"{Home}/.shipright/build/p1/build9", DockerBuildArgs(runner)![^1]);
    }

    [TestMethod]
    public async Task TheUsersFolderIsNeverTheBuildContext()
    {
        var runner = HealthyRunner();
        var project = Project(BuildSource.CleanSnapshot, ("Api", @"D:\work\shop\Api"));

        await new Testable(runner).Build(Record("Api"), project);

        Assert.IsFalse(DockerBuildArgs(runner)!.Any(a => a.Contains("D:\\")));
    }

    [TestMethod]
    public async Task TheCheckoutIsRemovedAfterASuccessfulBuild()
    {
        var runner = HealthyRunner();

        await new Testable(runner).Build(Record("Api"), Project(BuildSource.CleanSnapshot, ("Api", @"D:\work\shop\Api")));

        Assert.IsTrue(runner.Any($"worktree remove --force {Home}/.shipright/build/p1/build9"));
    }

    [TestMethod]
    public async Task TheCheckoutIsRemovedEvenWhenDockerFails()
    {
        var runner = HealthyRunner().When((e, a) => e == "docker" && a.Length > 0 && a[0] == "build", exit: 1);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            new Testable(runner).Build(Record("Api"), Project(BuildSource.CleanSnapshot, ("Api", @"D:\work\shop\Api"))));

        Assert.IsTrue(runner.Any($"worktree remove --force {Home}/.shipright/build/p1/build9"));
    }

    [TestMethod]
    public async Task TheBuildLogSaysWhichVersionIsBeingBuilt()
    {
        var record = Record("Api");

        await new Testable(HealthyRunner()).Build(record, Project(BuildSource.CleanSnapshot, ("Api", @"D:\work\shop\Api")));

        StringAssert.Contains(record.GetLogOutput(), Sha[..7]);
        StringAssert.Contains(record.GetLogOutput(), "clean copy");
    }

    // ── Never a dirty tree ───────────────────────────────────────────────────

    [TestMethod]
    public async Task UnsavedChanges_StopTheBuildWithAPlainExplanationAndTheFileNames()
    {
        var runner = HealthyRunner().WhenFirst((e, a) => e == "git" && a.Contains("status"), stdout: " M src/Program.cs\n?? NewService.cs\n");

        var ex = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            new Testable(runner).Build(Record("Api"), Project(BuildSource.CleanSnapshot, ("Api", @"D:\work\shop\Api"))));

        StringAssert.Contains(ex.Message, "src/Program.cs");
        StringAssert.Contains(ex.Message, "NewService.cs");
        StringAssert.Contains(ex.Message, "commit");
        Assert.IsFalse(runner.Any("worktree add"));
        Assert.IsNull(DockerBuildArgs(runner));
    }

    [TestMethod]
    public async Task AFolderThatIsNotARepo_StopsTheBuildWithTheReason()
    {
        var runner = HealthyRunner().WhenFirst((e, a) => e == "git" && a.Contains("status"), exit: 128, stderr: "fatal: not a git repository");

        var ex = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            new Testable(runner).Build(Record("Api"), Project(BuildSource.CleanSnapshot, ("Api", @"D:\work\shop\Api"))));

        StringAssert.Contains(ex.Message, "git repository");
        Assert.IsNull(DockerBuildArgs(runner));
    }

    [TestMethod]
    public async Task ARepoWithNoCommits_StopsTheBuild()
    {
        var runner = HealthyRunner().WhenFirst((e, a) => e == "git" && a.Contains("rev-parse"), exit: 128, stderr: "fatal: bad revision HEAD");

        var ex = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            new Testable(runner).Build(Record("Api"), Project(BuildSource.CleanSnapshot, ("Api", @"D:\work\shop\Api"))));

        StringAssert.Contains(ex.Message, "no commits");
    }

    // ── Environment problems ─────────────────────────────────────────────────

    [TestMethod]
    public async Task WslMissing_StopsTheBuildAndSaysSo_WithoutBuildingAnything()
    {
        var runner = HealthyRunner().WhenFirst((e, a) => e == "wsl" && a.Contains("printenv"), exit: 1, stderr: "no distro");

        var ex = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            new Testable(runner).Build(Record("Api"), Project(BuildSource.CleanSnapshot, ("Api", @"D:\work\shop\Api"))));

        StringAssert.Contains(ex.Message, "WSL");
        Assert.IsNull(DockerBuildArgs(runner));
    }

    [TestMethod]
    public async Task AServiceOutsideEveryRepo_IsNamedInTheError()
    {
        var runner = HealthyRunner();

        var ex = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            new Testable(runner).Build(Record("Api"), Project(BuildSource.CleanSnapshot, ("Api", @"E:\elsewhere\Api"))));

        StringAssert.Contains(ex.Message, "Api");
        StringAssert.Contains(ex.Message, "git");
        Assert.IsNull(DockerBuildArgs(runner));
    }

    // ── Several services and repos ───────────────────────────────────────────

    [TestMethod]
    public async Task OnlyServicesInThisBuildAreSnapshotted()
    {
        var runner = HealthyRunner();
        var project = Project(BuildSource.CleanSnapshot, ("Api", @"D:\work\shop\Api"), ("Web", @"E:\not\in\a\repo\Web"));

        await new Testable(runner).Build(Record("Api"), project);   // Web is not being built

        Assert.IsNotNull(DockerBuildArgs(runner));
        Assert.AreEqual(1, runner.Calls.Count(c => c.Args.Contains("worktree") && c.Args.Contains("add")));
    }

    [TestMethod]
    public async Task ServicesSharingARepo_ShareOneCheckout()
    {
        var runner = HealthyRunner();
        var project = Project(BuildSource.CleanSnapshot, ("Api", @"D:\work\shop\Api"), ("Web", @"D:\work\shop\Web"));

        await new Testable(runner).Build(Record("Api", "Web"), project);

        Assert.AreEqual(1, runner.Calls.Count(c => c.Args.Contains("worktree") && c.Args.Contains("add")));
        Assert.AreEqual($"{Home}/.shipright/build/p1/build9/Api", DockerBuildArgs(runner, 0)![^1]);
        Assert.AreEqual($"{Home}/.shipright/build/p1/build9/Web", DockerBuildArgs(runner, 1)![^1]);
    }

    [TestMethod]
    public async Task ServicesInSeparateRepos_EachGetTheirOwnCheckoutOfTheirOwnCommit()
    {
        var runner = new RecordingRunner()
            .When((e, a) => e == "git" && a.Contains("status"), stdout: "")
            .When((e, a) => e == "git" && a.Contains("rev-parse") && a.Contains(@"D:\work\shop-web"), stdout: Sha2 + "\n")
            .When((e, a) => e == "git" && a.Contains("rev-parse"), stdout: Sha + "\n")
            .When((e, a) => e == "wsl" && a.Contains("printenv"), stdout: Home + "\n")
            .When((e, a) => e == "docker" && a.Contains("inspect"), exit: 1);
        var project = Project(BuildSource.CleanSnapshot, ("Api", @"D:\work\shop\Api"), ("Web", @"D:\work\shop-web"))
            with { GitRepos = [new GitConfig { RepoPath = @"D:\work\shop" }, new GitConfig { RepoPath = @"D:\work\shop-web" }] };

        await new Testable(runner).Build(Record("Api", "Web"), project);

        Assert.AreEqual(2, runner.Calls.Count(c => c.Args.Contains("worktree") && c.Args.Contains("add")));
        Assert.IsTrue(runner.Any(Sha));
        Assert.IsTrue(runner.Any(Sha2));
        Assert.AreEqual(2, runner.Calls.Count(c => c.Args.Contains("worktree") && c.Args.Contains("remove")));
    }

    [TestMethod]
    public async Task WhenOneOfTwoReposIsDirty_NothingIsBuiltAndNothingIsLeftBehind()
    {
        var runner = new RecordingRunner()
            .When((e, a) => e == "git" && a.Contains("status") && a.Contains(@"D:\work\shop-web"), stdout: " M x.js\n")
            .When((e, a) => e == "git" && a.Contains("status"), stdout: "")
            .When((e, a) => e == "git" && a.Contains("rev-parse"), stdout: Sha + "\n")
            .When((e, a) => e == "wsl" && a.Contains("printenv"), stdout: Home + "\n");
        var project = Project(BuildSource.CleanSnapshot, ("Api", @"D:\work\shop\Api"), ("Web", @"D:\work\shop-web"))
            with { GitRepos = [new GitConfig { RepoPath = @"D:\work\shop" }, new GitConfig { RepoPath = @"D:\work\shop-web" }] };

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            new Testable(runner).Build(Record("Api", "Web"), project));

        Assert.IsNull(DockerBuildArgs(runner));
        Assert.IsFalse(runner.Any("worktree add"), "every repo is checked before any checkout is made");
    }

    [TestMethod]
    public async Task ARepoPathThatIsAPrefixOfAnotherFolderName_IsNotConfusedWithIt()
    {
        var runner = HealthyRunner();
        // D:\work\shop must not claim D:\work\shop-web\Web
        var project = Project(BuildSource.CleanSnapshot, ("Web", @"D:\work\shop-web\Web"));

        var ex = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            new Testable(runner).Build(Record("Web"), project));

        StringAssert.Contains(ex.Message, "Web");
    }

    [TestMethod]
    public async Task RepoMatching_IgnoresCaseAndSlashStyle()
    {
        var runner = HealthyRunner();
        var project = Project(BuildSource.CleanSnapshot, ("Api", "d:/WORK/shop/Api/"));

        await new Testable(runner).Build(Record("Api"), project);

        Assert.AreEqual($"{Home}/.shipright/build/p1/build9/Api", DockerBuildArgs(runner)![^1]);
    }
}
