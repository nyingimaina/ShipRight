using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Setup;

namespace ShipRight.Tests.Modules.Setup;

[TestClass]
public class SetupApplierTests
{
    private const string WebCsproj =
        "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>";

    private static SetupPlan PlanFor(TempTree t) => SetupPlanner.CreateDefault().Plan(t.Root);

    private static IReadOnlyCollection<string> AllIds(SetupPlan plan) =>
        plan.Items.Where(i => i.Status == PlanItemStatus.WillCreate).Select(i => i.Id).ToList();

    private static ApplyItemResult Result(ApplyResult r, SetupPlan plan, PlanItemKind kind) =>
        r.Items.Single(i => i.ItemId == plan.Items.Single(p => p.Kind == kind).Id);

    [TestMethod]
    public async Task ApprovedFileItems_AreWrittenWithTheirPlannedContent()
    {
        using var t = new TempTree();
        t.Add("Api/Api.csproj", WebCsproj);
        var plan = PlanFor(t);
        var runner = new RecordingRunner();

        var result = await new SetupApplier(runner).ApplyAsync(plan, new ApplyOptions(AllIds(plan)));

        Assert.AreEqual(ApplyOutcome.Created, Result(result, plan, PlanItemKind.Dockerfile).Outcome);
        var dockerfile = plan.Items.Single(i => i.Kind == PlanItemKind.Dockerfile);
        Assert.AreEqual(dockerfile.Content, File.ReadAllText(dockerfile.Path));
        Assert.IsTrue(File.Exists(t.Full("Api/version.txt")));
        Assert.IsTrue(File.Exists(t.Full(".gitignore")));
        Assert.IsTrue(File.Exists(t.Full("Api/.dockerignore")));
    }

    [TestMethod]
    public async Task WrittenFiles_UseLfLineEndingsAndNoByteOrderMark()
    {
        using var t = new TempTree();
        t.Add("Api/Api.csproj", WebCsproj);
        var plan = PlanFor(t);

        await new SetupApplier(new RecordingRunner()).ApplyAsync(plan, new ApplyOptions(AllIds(plan)));

        var bytes = File.ReadAllBytes(t.Full("Api/Dockerfile"));
        Assert.IsFalse(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        Assert.IsFalse(bytes.Contains((byte)'\r'));
    }

    [TestMethod]
    public async Task ItemsThatWereNotApproved_AreNotWritten()
    {
        using var t = new TempTree();
        t.Add("Api/Api.csproj", WebCsproj);
        var plan = PlanFor(t);
        var approved = AllIds(plan).Where(id => !id.StartsWith("Dockerfile")).ToList();

        var result = await new SetupApplier(new RecordingRunner()).ApplyAsync(plan, new ApplyOptions(approved));

        Assert.AreEqual(ApplyOutcome.SkippedNotApproved, Result(result, plan, PlanItemKind.Dockerfile).Outcome);
        Assert.IsFalse(File.Exists(t.Full("Api/Dockerfile")));
    }

    [TestMethod]
    public async Task AFileThatAppearedAfterPlanning_IsNeverOverwritten()
    {
        using var t = new TempTree();
        t.Add("Api/Api.csproj", WebCsproj);
        var plan = PlanFor(t);
        t.Add("Api/Dockerfile", "MY OWN DOCKERFILE");

        var result = await new SetupApplier(new RecordingRunner()).ApplyAsync(plan, new ApplyOptions(AllIds(plan)));

        Assert.AreEqual(ApplyOutcome.SkippedExists, Result(result, plan, PlanItemKind.Dockerfile).Outcome);
        Assert.AreEqual("MY OWN DOCKERFILE", File.ReadAllText(t.Full("Api/Dockerfile")));
    }

    [TestMethod]
    public async Task ItemsAlreadyPresentAtPlanTime_AreNotTouchedEvenIfApproved()
    {
        using var t = new TempTree();
        t.Add("Api/Api.csproj", WebCsproj);
        t.Add("Api/Dockerfile", "MINE");
        var plan = PlanFor(t);
        var everyId = plan.Items.Select(i => i.Id).ToList();

        var result = await new SetupApplier(new RecordingRunner()).ApplyAsync(plan, new ApplyOptions(everyId));

        Assert.AreEqual(ApplyOutcome.SkippedExists, Result(result, plan, PlanItemKind.Dockerfile).Outcome);
        Assert.AreEqual("MINE", File.ReadAllText(t.Full("Api/Dockerfile")));
    }

    [TestMethod]
    public async Task GitRepoItem_RunsGitInitOnTheMainBranchInTheRepoRoot()
    {
        using var t = new TempTree();
        t.Add("Api/Api.csproj", WebCsproj);
        var plan = PlanFor(t);
        var runner = new RecordingRunner();

        var result = await new SetupApplier(runner).ApplyAsync(plan, new ApplyOptions(AllIds(plan)));

        Assert.AreEqual(ApplyOutcome.Created, Result(result, plan, PlanItemKind.GitRepo).Outcome);
        Assert.IsTrue(runner.Any("init"));
        Assert.IsTrue(runner.Any("--initial-branch=main"));
        Assert.IsTrue(runner.Any(t.Root));
    }

    [TestMethod]
    public async Task GitInit_FallsBackToPlainInitWhenTheGitVersionIsTooOldForInitialBranch()
    {
        using var t = new TempTree();
        t.Add("Api/Api.csproj", WebCsproj);
        var plan = PlanFor(t);
        var runner = new RecordingRunner().WhenArgs("--initial-branch=main", exit: 129, stderr: "unknown option");

        var result = await new SetupApplier(runner).ApplyAsync(plan, new ApplyOptions(AllIds(plan)));

        Assert.AreEqual(ApplyOutcome.Created, Result(result, plan, PlanItemKind.GitRepo).Outcome);
        Assert.AreEqual(2, runner.Calls.Count(c => c.Args.Contains("init")));
    }

    [TestMethod]
    public async Task GitInitFailure_IsReportedAsFailedWithGitsMessage_AndFileItemsStillProceed()
    {
        using var t = new TempTree();
        t.Add("Api/Api.csproj", WebCsproj);
        var plan = PlanFor(t);
        var runner = new RecordingRunner().WhenArgs("init", exit: 1, stderr: "fatal: no git here");

        var result = await new SetupApplier(runner).ApplyAsync(plan, new ApplyOptions(AllIds(plan)));

        var git = Result(result, plan, PlanItemKind.GitRepo);
        Assert.AreEqual(ApplyOutcome.Failed, git.Outcome);
        StringAssert.Contains(git.Message, "no git here");
        Assert.AreEqual(ApplyOutcome.Created, Result(result, plan, PlanItemKind.Dockerfile).Outcome);
    }

    [TestMethod]
    public async Task GitInit_IsSkippedWhenARepoAppearedAfterPlanning()
    {
        using var t = new TempTree();
        t.Add("Api/Api.csproj", WebCsproj);
        var plan = PlanFor(t);
        t.Dir(".git");
        var runner = new RecordingRunner();

        var result = await new SetupApplier(runner).ApplyAsync(plan, new ApplyOptions(AllIds(plan)));

        Assert.AreEqual(ApplyOutcome.SkippedExists, Result(result, plan, PlanItemKind.GitRepo).Outcome);
        Assert.IsFalse(runner.Any("init"));
    }

    [TestMethod]
    public async Task ItemPathOutsideTheRepo_IsRejectedAndNothingIsWritten()
    {
        using var t = new TempTree();
        var outside = Path.Combine(Path.GetDirectoryName(t.Root)!, "outside.txt");
        var evil = new PlanItem("VersionFile:../outside.txt", PlanItemKind.VersionFile, PlanItemStatus.WillCreate,
            outside, "../outside.txt", "x", "pwned", null);
        var plan = new SetupPlan(t.Root, t.Root, [], [evil], []);

        var result = await new SetupApplier(new RecordingRunner()).ApplyAsync(plan, new ApplyOptions([evil.Id]));

        Assert.AreEqual(ApplyOutcome.Failed, result.Items.Single().Outcome);
        Assert.IsFalse(File.Exists(outside));
    }

    [TestMethod]
    public async Task ItemPathThatTraversesOutViaDotDot_IsRejected()
    {
        using var t = new TempTree();
        var sneaky = Path.Combine(t.Root, "..", "sneaky.txt");
        var evil = new PlanItem("VersionFile:sneaky", PlanItemKind.VersionFile, PlanItemStatus.WillCreate,
            sneaky, "../sneaky.txt", "x", "pwned", null);
        var plan = new SetupPlan(t.Root, t.Root, [], [evil], []);

        var result = await new SetupApplier(new RecordingRunner()).ApplyAsync(plan, new ApplyOptions([evil.Id]));

        Assert.AreEqual(ApplyOutcome.Failed, result.Items.Single().Outcome);
        Assert.IsFalse(File.Exists(Path.GetFullPath(sneaky)));
    }

    [TestMethod]
    public async Task Applying_NeverStagesOrCommitsByDefault()
    {
        using var t = new TempTree();
        t.Add("Api/Api.csproj", WebCsproj);
        var plan = PlanFor(t);
        var runner = new RecordingRunner();

        var result = await new SetupApplier(runner).ApplyAsync(plan, new ApplyOptions(AllIds(plan)));

        Assert.IsFalse(result.Committed);
        Assert.IsFalse(runner.Any(" add "));
        Assert.IsFalse(runner.Any("commit"));
    }

    [TestMethod]
    public async Task WhenAskedToCommit_OnlyTheCreatedFilesAreStagedAndCommitted()
    {
        using var t = new TempTree();
        t.Add("Api/Api.csproj", WebCsproj);
        t.Add("Api/Dockerfile", "MINE");
        var plan = PlanFor(t);
        var runner = new RecordingRunner();

        var result = await new SetupApplier(runner).ApplyAsync(plan, new ApplyOptions(AllIds(plan), CommitCreatedFiles: true));

        Assert.IsTrue(result.Committed);
        var add = runner.Calls.Single(c => c.Args.Contains("add"));
        Assert.IsTrue(add.Args.Contains("--"));
        Assert.IsTrue(add.Args.Contains(".gitignore"));
        Assert.IsFalse(add.Args.Contains("Api/Dockerfile"), "an existing Dockerfile must not be staged");
        Assert.IsFalse(add.Args.Contains("-A") || add.Args.Contains("."), "never stage everything");
        Assert.IsTrue(runner.Calls.Any(c => c.Args.Contains("commit") && c.Args.Contains("-m")));
    }

    [TestMethod]
    public async Task CommitFailure_IsReportedWithoutThrowing_AndTheFilesStayOnDisk()
    {
        using var t = new TempTree();
        t.Add("Api/Api.csproj", WebCsproj);
        var plan = PlanFor(t);
        var runner = new RecordingRunner().WhenArgs("commit", exit: 128, stderr: "Please tell me who you are.");

        var result = await new SetupApplier(runner).ApplyAsync(plan, new ApplyOptions(AllIds(plan), CommitCreatedFiles: true));

        Assert.IsFalse(result.Committed);
        StringAssert.Contains(result.CommitError!, "who you are");
        Assert.IsTrue(File.Exists(t.Full("Api/Dockerfile")));
    }

    [TestMethod]
    public async Task NothingCreated_MeansNothingToCommit()
    {
        using var t = new TempTree();
        t.Add("Api/Api.csproj", WebCsproj);
        var plan = PlanFor(t);
        var runner = new RecordingRunner();

        var result = await new SetupApplier(runner).ApplyAsync(plan, new ApplyOptions([], CommitCreatedFiles: true));

        Assert.IsFalse(result.Committed);
        Assert.IsFalse(runner.Any("commit"));
    }
}
