using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Setup;

namespace ShipRight.Tests.Modules.Setup;

[TestClass]
public class SetupPlannerTests
{
    private const string WebCsproj =
        "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>";

    private static SetupPlan Plan(TempTree t) => SetupPlanner.CreateDefault().Plan(t.Root);

    private static PlanItem Item(SetupPlan plan, PlanItemKind kind) => plan.Items.Single(i => i.Kind == kind);

    [TestMethod]
    public void EmptyFolder_PlansNothingExceptAWarningThatNothingWasRecognised()
    {
        using var t = new TempTree();

        var plan = Plan(t);

        Assert.AreEqual(0, plan.Components.Count);
        Assert.AreEqual(0, plan.Items.Count);
        Assert.IsTrue(plan.Warnings.Any(w => w.Contains("recognise")));
    }

    [TestMethod]
    public void FreshDotNetProject_PlansEverythingItNeeds()
    {
        using var t = new TempTree();
        t.Add("Api/Api.csproj", WebCsproj);

        var plan = Plan(t);

        Assert.AreEqual(1, plan.Components.Count);
        Assert.AreEqual(PlanItemStatus.WillCreate, Item(plan, PlanItemKind.GitRepo).Status);
        Assert.AreEqual(PlanItemStatus.WillCreate, Item(plan, PlanItemKind.GitIgnore).Status);
        Assert.AreEqual(PlanItemStatus.WillCreate, Item(plan, PlanItemKind.GitAttributes).Status);
        Assert.AreEqual(PlanItemStatus.WillCreate, Item(plan, PlanItemKind.Dockerfile).Status);
        Assert.AreEqual(PlanItemStatus.WillCreate, Item(plan, PlanItemKind.DockerIgnore).Status);
        Assert.AreEqual(PlanItemStatus.WillCreate, Item(plan, PlanItemKind.VersionFile).Status);
    }

    [TestMethod]
    public void EveryWillCreateFileItem_CarriesItsContentAndAPlainReason()
    {
        using var t = new TempTree();
        t.Add("Api/Api.csproj", WebCsproj);

        var plan = Plan(t);

        foreach (var item in plan.Items.Where(i => i.Kind != PlanItemKind.GitRepo && i.Status == PlanItemStatus.WillCreate))
        {
            Assert.IsFalse(string.IsNullOrEmpty(item.Content), item.Id);
            Assert.IsFalse(string.IsNullOrWhiteSpace(item.Reason), item.Id);
        }
    }

    [TestMethod]
    public void DockerfilePlan_PointsAtTheContextFolderAndCarriesTheRenderedText()
    {
        using var t = new TempTree();
        t.Add("Api/Api.csproj", WebCsproj);

        var item = Item(Plan(t), PlanItemKind.Dockerfile);

        Assert.AreEqual(t.Full("Api/Dockerfile"), item.Path);
        Assert.AreEqual("Api/Dockerfile", item.RelativePath);
        Assert.AreEqual("Api", item.ComponentName);
        StringAssert.Contains(item.Content!, "dotnet publish");
    }

    [TestMethod]
    public void ItemIds_AreStableAndUnique()
    {
        using var t = new TempTree();
        t.Add("Api/Api.csproj", WebCsproj);
        t.Add("Web/package.json", "{ \"name\": \"web\", \"dependencies\": { \"next\": \"15.0.0\" } }");

        var first = Plan(t).Items.Select(i => i.Id).ToList();
        var second = Plan(t).Items.Select(i => i.Id).ToList();

        CollectionAssert.AreEqual(first, second);
        Assert.AreEqual(first.Count, first.Distinct().Count());
    }

    [TestMethod]
    public void ExistingDockerfile_IsReportedAsExistingAndNeverPlannedForWriting()
    {
        using var t = new TempTree();
        t.Add("Api/Api.csproj", WebCsproj);
        t.Add("Api/Dockerfile", "FROM scratch");

        var item = Item(Plan(t), PlanItemKind.Dockerfile);

        Assert.AreEqual(PlanItemStatus.Exists, item.Status);
        Assert.IsNull(item.Content);
    }

    [TestMethod]
    public void ExistingDockerfileWithDifferentCase_IsStillRecognised()
    {
        using var t = new TempTree();
        t.Add("Api/Api.csproj", WebCsproj);
        t.Add("Api/DOCKERFILE", "FROM scratch");

        Assert.AreEqual(PlanItemStatus.Exists, Item(Plan(t), PlanItemKind.Dockerfile).Status);
    }

    [TestMethod]
    public void ExistingVersionFile_IsLeftAlone()
    {
        using var t = new TempTree();
        t.Add("Api/Api.csproj", WebCsproj);
        t.Add("Api/version.txt", "3.1.4");

        Assert.AreEqual(PlanItemStatus.Exists, Item(Plan(t), PlanItemKind.VersionFile).Status);
    }

    [TestMethod]
    public void NewVersionFile_StartsFromTheProjectsDeclaredVersion()
    {
        using var t = new TempTree();
        t.Add("Api/Api.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><Version>2.5.1</Version></PropertyGroup></Project>");

        Assert.AreEqual("2.5.1\n", Item(Plan(t), PlanItemKind.VersionFile).Content);
    }

    [TestMethod]
    public void NewVersionFile_WithNoDeclaredVersion_StartsAtZeroOneZero()
    {
        using var t = new TempTree();
        t.Add("Api/Api.csproj", WebCsproj);

        Assert.AreEqual("0.1.0\n", Item(Plan(t), PlanItemKind.VersionFile).Content);
    }

    [TestMethod]
    public void VersionFile_IsPlacedBesideTheDockerfileSoShipRightPairsThem()
    {
        using var t = new TempTree();
        t.Add("App.sln", "");
        t.Add("src/Api/Api.csproj", WebCsproj);

        var plan = Plan(t);

        Assert.AreEqual(t.Full("version.txt"), Item(plan, PlanItemKind.VersionFile).Path);
        Assert.AreEqual(t.Full("Dockerfile"), Item(plan, PlanItemKind.Dockerfile).Path);
    }

    // ── Git ──────────────────────────────────────────────────────────────────

    [TestMethod]
    public void FolderInsideAnExistingRepo_DoesNotPlanGitInit()
    {
        using var t = new TempTree();
        t.Dir(".git");
        t.Add("Api/Api.csproj", WebCsproj);

        var plan = Plan(t);

        Assert.AreEqual(PlanItemStatus.Exists, Item(plan, PlanItemKind.GitRepo).Status);
        Assert.AreEqual(t.Root, plan.RepoRoot);
    }

    [TestMethod]
    public void FolderInsideAParentRepo_UsesThatRepo_AndSaysSo()
    {
        using var t = new TempTree();
        var sandbox = Path.GetDirectoryName(t.Root)!;
        Directory.CreateDirectory(Path.Combine(sandbox, ".git"));
        t.Add("Api/Api.csproj", WebCsproj);

        var plan = Plan(t);

        Assert.AreEqual(sandbox, plan.RepoRoot);
        Assert.AreEqual(PlanItemStatus.Exists, Item(plan, PlanItemKind.GitRepo).Status);
        Assert.IsTrue(plan.Warnings.Any(w => w.Contains("larger git repository")));
    }

    [TestMethod]
    public void GitFileAsInSubmodulesAndWorktrees_CountsAsARepo()
    {
        using var t = new TempTree();
        t.Add(".git", "gitdir: ../elsewhere");
        t.Add("Api/Api.csproj", WebCsproj);

        Assert.AreEqual(PlanItemStatus.Exists, Item(Plan(t), PlanItemKind.GitRepo).Status);
    }

    [TestMethod]
    public void NoRepo_PlansGitInitAtTheProjectFolder()
    {
        using var t = new TempTree();
        t.Add("Api/Api.csproj", WebCsproj);

        var plan = Plan(t);

        Assert.AreEqual(t.Root, plan.RepoRoot);
        Assert.AreEqual(t.Full(".git"), Item(plan, PlanItemKind.GitRepo).Path);
    }

    [TestMethod]
    public void NewRepo_WillStartOnTheMainBranch()
    {
        using var t = new TempTree();
        t.Add("Api/Api.csproj", WebCsproj);

        Assert.AreEqual("main", Plan(t).RepoBranch);
    }

    [TestMethod]
    public void ExistingRepo_ReportsItsCurrentBranch()
    {
        using var t = new TempTree();
        t.Add(".git/HEAD", "ref: refs/heads/develop\n");
        t.Add("Api/Api.csproj", WebCsproj);

        Assert.AreEqual("develop", Plan(t).RepoBranch);
    }

    [TestMethod]
    public void ExistingRepoWithBranchNamesContainingSlashes_KeepsTheWholeName()
    {
        using var t = new TempTree();
        t.Add(".git/HEAD", "ref: refs/heads/release/2.0\n");
        t.Add("Api/Api.csproj", WebCsproj);

        Assert.AreEqual("release/2.0", Plan(t).RepoBranch);
    }

    [TestMethod]
    public void ExistingRepoOnADetachedHead_HasNoKnownBranch()
    {
        using var t = new TempTree();
        t.Add(".git/HEAD", "3f2a9c1d0e8b7a6f5e4d3c2b1a0f9e8d7c6b5a4f\n");
        t.Add("Api/Api.csproj", WebCsproj);

        Assert.IsNull(Plan(t).RepoBranch);
    }

    [TestMethod]
    public void GitFileRepo_HasNoKnownBranch()
    {
        using var t = new TempTree();
        t.Add(".git", "gitdir: ../elsewhere");
        t.Add("Api/Api.csproj", WebCsproj);

        Assert.IsNull(Plan(t).RepoBranch);
    }

    // ── .gitignore / .gitattributes ──────────────────────────────────────────

    [TestMethod]
    public void NewGitIgnore_CoversSecretsAndTheStacksBuildOutput()
    {
        using var t = new TempTree();
        t.Add("Api/Api.csproj", WebCsproj);
        t.Add("Web/package.json", "{ \"name\": \"web\", \"dependencies\": { \"next\": \"15.0.0\" } }");

        var text = Item(Plan(t), PlanItemKind.GitIgnore).Content!;

        foreach (var entry in new[] { ".env", "!.env.example", "bin/", "obj/", "node_modules/", ".next/" })
            StringAssert.Contains(text, entry);
    }

    [TestMethod]
    public void NewGitIgnore_OnlyIncludesEntriesForStacksThatWereFound()
    {
        using var t = new TempTree();
        t.Add("Api/Api.csproj", WebCsproj);

        var text = Item(Plan(t), PlanItemKind.GitIgnore).Content!;

        Assert.IsFalse(text.Contains("node_modules/"));
    }

    [TestMethod]
    public void ExistingGitIgnore_IsNeverRewritten()
    {
        using var t = new TempTree();
        t.Dir(".git");
        t.Add(".gitignore", ".env\nbin/\n");
        t.Add("Api/Api.csproj", WebCsproj);

        var item = Item(Plan(t), PlanItemKind.GitIgnore);

        Assert.AreEqual(PlanItemStatus.Exists, item.Status);
        Assert.IsNull(item.Content);
    }

    [TestMethod]
    public void ExistingGitIgnoreThatDoesNotCoverEnvFiles_RaisesASecretsWarning()
    {
        using var t = new TempTree();
        t.Dir(".git");
        t.Add(".gitignore", "bin/\nobj/\n");
        t.Add("Api/Api.csproj", WebCsproj);

        Assert.IsTrue(Plan(t).Warnings.Any(w => w.Contains(".env") && w.Contains(".gitignore")));
    }

    [TestMethod]
    [DataRow(".env")]
    [DataRow(".env*")]
    [DataRow(".env.*")]
    [DataRow("*.env")]
    public void ExistingGitIgnoreThatCoversEnvFiles_RaisesNoSecretsWarning(string pattern)
    {
        using var t = new TempTree();
        t.Dir(".git");
        t.Add(".gitignore", pattern + "\n");
        t.Add("Api/Api.csproj", WebCsproj);

        Assert.IsFalse(Plan(t).Warnings.Any(w => w.Contains(".gitignore")));
    }

    [TestMethod]
    public void NewGitAttributes_KeepsShellScriptsAndDockerfilesOnLfLineEndings()
    {
        using var t = new TempTree();
        t.Add("Api/Api.csproj", WebCsproj);

        var text = Item(Plan(t), PlanItemKind.GitAttributes).Content!;

        StringAssert.Contains(text, "*.sh text eol=lf");
        StringAssert.Contains(text, "Dockerfile* text eol=lf");
    }

    // ── Several components ───────────────────────────────────────────────────

    [TestMethod]
    public void MonorepoWithApiAndWeb_GetsOneDockerfileEach_AndASingleSharedGitSetup()
    {
        using var t = new TempTree();
        t.Add("back-end/Api/Api.csproj", WebCsproj);
        t.Add("front-end/package.json", "{ \"name\": \"ui\", \"dependencies\": { \"next\": \"15.0.0\" } }");

        var plan = Plan(t);

        Assert.AreEqual(2, plan.Components.Count);
        Assert.AreEqual(2, plan.Items.Count(i => i.Kind == PlanItemKind.Dockerfile));
        Assert.AreEqual(1, plan.Items.Count(i => i.Kind == PlanItemKind.GitRepo));
        Assert.AreEqual(1, plan.Items.Count(i => i.Kind == PlanItemKind.GitIgnore));
    }

    [TestMethod]
    public void TwoComponentsSharingOneContext_WarnInsteadOfPlanningADuplicateDockerfile()
    {
        using var t = new TempTree();
        t.Add("App.sln", "");
        t.Add("src/Api/Api.csproj", WebCsproj);
        t.Add("src/Admin/Admin.csproj", WebCsproj);

        var plan = Plan(t);

        Assert.AreEqual(1, plan.Items.Count(i => i.Kind == PlanItemKind.Dockerfile));
        Assert.IsTrue(plan.Warnings.Any(w => w.Contains("Admin") && w.Contains("Dockerfile")));
    }

    [TestMethod]
    public void ProviderWarnings_AreCarriedIntoThePlan()
    {
        using var t = new TempTree();
        t.Add("Bad/Bad.csproj", "<Project><oops");

        Assert.IsTrue(Plan(t).Warnings.Any(w => w.Contains("Bad.csproj")));
    }

    [TestMethod]
    public void ComponentNotes_AreCarriedIntoThePlanWithTheComponentName()
    {
        using var t = new TempTree();
        t.Add("ui/package.json",
            "{ \"name\": \"ui\", \"scripts\": { \"build\": \"ng build\" }, \"devDependencies\": { \"@angular/cli\": \"19.0.0\" } }");

        Assert.IsTrue(Plan(t).Warnings.Any(w => w.StartsWith("ui:")));
    }

    [TestMethod]
    public void ARepeatedPlan_ChangesNothingOnDisk()
    {
        using var t = new TempTree();
        t.Add("Api/Api.csproj", WebCsproj);
        var before = Directory.GetFileSystemEntries(t.Root, "*", SearchOption.AllDirectories).Length;

        Plan(t);
        Plan(t);

        Assert.AreEqual(before, Directory.GetFileSystemEntries(t.Root, "*", SearchOption.AllDirectories).Length);
    }

    [TestMethod]
    public void MissingRootFolder_Throws()
    {
        Assert.ThrowsException<DirectoryNotFoundException>(() =>
            SetupPlanner.CreateDefault().Plan(Path.Combine(Path.GetTempPath(), "sr_does_not_exist_" + Guid.NewGuid().ToString("N"))));
    }
}
