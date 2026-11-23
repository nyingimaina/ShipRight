using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Builds;
using ShipRight.Modules.JitDeploy;
using ShipRight.Modules.Projects;
using ShipRight.Shared.Events;
using ShipRight.Tests.Modules.Setup;

namespace ShipRight.Tests.Modules.JitDeploy;

[TestClass]
public class JitDeployServiceTests
{
    private sealed class Projects : IProjectStore
    {
        public Dictionary<string, ProjectConfig> Items { get; } = [];
        public int Count => Items.Count;
        public Task<List<ProjectConfig>> GetAllAsync() => Task.FromResult(Items.Values.ToList());
        public Task<ProjectConfig?> GetByIdAsync(string id) => Task.FromResult(Items.GetValueOrDefault(id));
        public Task<ProjectConfig?> GetByNameAsync(string name) => Task.FromResult(Items.Values.FirstOrDefault(p => p.Name == name));
        public Task SaveAsync(ProjectConfig p) { Items[p.Id] = p; return Task.CompletedTask; }
        public Task DeleteAsync(string id) { Items.Remove(id); return Task.CompletedTask; }
    }

    private sealed class Builds : IBuildStore
    {
        public Dictionary<string, BuildRecord> Items { get; } = [];
        public int Count => Items.Count;
        public Task SaveAsync(BuildRecord r) { Items[r.Id] = r; return Task.CompletedTask; }
        public Task<BuildRecord?> GetByIdAsync(string id) => Task.FromResult(Items.GetValueOrDefault(id));
        public Task<List<BuildRecord>> QueryAsync(string? projectId, string? status, DateTime? from, DateTime? to, string? gitTag, int page, int pageSize) => Task.FromResult(new List<BuildRecord>());
        public Task<int> CountQueryAsync(string? projectId, string? status, DateTime? from, DateTime? to, string? gitTag) => Task.FromResult(0);
        public Task MarkInterruptedAsync() => Task.CompletedTask;
    }

    private sealed class Configs : IJitConfigStore
    {
        public Dictionary<string, JitDeployConfig> Items { get; } = [];
        public Task<JitDeployConfig?> GetAsync(string projectId) => Task.FromResult(Items.GetValueOrDefault(projectId));
        public Task SaveAsync(string projectId, JitDeployConfig config) { Items[projectId] = config; return Task.CompletedTask; }
        public Task DeleteAsync(string projectId) { Items.Remove(projectId); return Task.CompletedTask; }
    }

    private sealed class Rig
    {
        public Projects ProjectStore { get; } = new();
        public Builds BuildStore { get; } = new();
        public Configs ConfigStore { get; } = new();
        public BuildEventBus Bus { get; } = new();
        public FakeRemote Remote { get; } = new();
        public FakeClock Clock { get; } = new();
        public JitDeployService Service { get; }

        public Rig(bool enabled = true, JitLocalBranchGuard? localBranchGuard = null)
        {
            ProjectStore.Items["p1"] = new ProjectConfig
            {
                Id = "p1", Name = "Shop",
                Services =
                [
                    new ServiceConfig { Name = "api", DockerImageName = "shop/api", ComposeServiceName = "api" },
                    new ServiceConfig { Name = "web", DockerImageName = "shop/web", ComposeServiceName = "web" },
                ],
                Server = new ServerConfig { Host = "10.0.0.5", Username = "deploy", SshKeyPath = "/keys/k", RemoteWorkingDir = "/home/deploy/shop" },
            };
            ConfigStore.Items["p1"] = new JitDeployConfig { Enabled = enabled, RepoUrl = "git@github.com:o/r.git", ComposeProject = "shop" };
            Service = new JitDeployService(ProjectStore, ConfigStore, BuildStore, Bus, Remote, Clock, new JitDeployLock(), null, localBranchGuard);
        }

        public Rig WithLocalRepo(string repoPath = @"D:\work\shop") =>
            this.With(items => items["p1"] = items["p1"] with
            {
                GitRepos = [new GitConfig { RepoPath = repoPath, DeployBranch = "master" }]
            });

        private Rig With(Action<Dictionary<string, ProjectConfig>> change)
        {
            change(ProjectStore.Items);
            return this;
        }

        public async Task<BuildRecord> DeployAsync(JitDeployOptions? options = null)
        {
            var start = await Service.StartDeployAsync("p1", options, "tester");
            Assert.IsNull(start.Error, start.Error);
            await start.Completion!;
            return (await BuildStore.GetByIdAsync(start.Record!.Id))!;
        }
    }

    // ═══ deploying ══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task ADeploy_IsRecordedAsABuild_SoHistoryAndTheLogViewerWorkUnchanged()
    {
        var rig = new Rig();
        var start = await rig.Service.StartDeployAsync("p1", null, "tester");

        Assert.IsNull(start.Error);
        Assert.IsNotNull(await rig.BuildStore.GetByIdAsync(start.Record!.Id), "the record exists as soon as the deploy starts");
        await start.Completion!;

        var rec = (await rig.BuildStore.GetByIdAsync(start.Record.Id))!;
        Assert.AreEqual(BuildStatus.Deployed, rec.Status);
        Assert.AreEqual("p1", rec.ProjectId);
        Assert.AreEqual("Shop", rec.ProjectName);
        Assert.AreEqual("tester", rec.Initiator);
        Assert.AreEqual("r1-aaaaaaa", rec.GitTag);
        Assert.IsNotNull(rec.DeployedAt);
        Assert.IsFalse(rec.IsRollback);
        CollectionAssert.AreEquivalent(new[] { "api", "web" }, rec.Versions.Select(v => v.ServiceName).ToArray());
        Assert.IsTrue(rec.Versions.All(v => v.NewVersion == "r1-aaaaaaa"));
        Assert.IsTrue(rec.StepDurations.ContainsKey("Deploy"));
        StringAssert.Contains(rec.GetLogOutput(), "[deploy] Release r1-aaaaaaa is live");
    }

    [TestMethod]
    public async Task UnpushedLocalWork_RefusesTheDeployBeforeTouchingTheServer()
    {
        var runner = new RecordingRunner()
            .When((_, a) => a.Contains("remote") && a.Contains("get-url"), 0, "git@github.com:o/r.git\n")
            .When((_, a) => a.Contains("--abbrev-ref"), 0, "master\n")
            .When((_, a) => a.Any(x => x.StartsWith("--porcelain", StringComparison.Ordinal)), 0, "M  Program.cs\n")
            .When((_, a) => a.Contains("--verify"), 0, FakeRemote.Sha1 + "\n")
            .When((_, a) => a.Contains("rev-list"), 0, "0 1\n");
        var rig = new Rig(localBranchGuard: new JitLocalBranchGuard(runner)).WithLocalRepo();

        var start = await rig.Service.StartDeployAsync("p1", null, "tester");

        Assert.IsNotNull(start.Error);
        StringAssert.Contains(start.Error!, "commit", StringComparison.OrdinalIgnoreCase);
        Assert.IsNull(start.Record);
        Assert.IsNull(start.Completion);
        Assert.AreEqual(0, rig.BuildStore.Count, "no build record is created for a deploy that never left the machine");
        Assert.AreEqual(0, rig.Remote.Commands.Count, "the server is not contacted at all");
    }

    [TestMethod]
    public async Task CleanPushedBranch_StillDeploys()
    {
        var runner = new RecordingRunner()
            .When((_, a) => a.Contains("remote") && a.Contains("get-url"), 0, "git@github.com:o/r.git\n")
            .When((_, a) => a.Contains("--abbrev-ref"), 0, "master\n")
            .When((_, a) => a.Any(x => x.StartsWith("--porcelain", StringComparison.Ordinal)), 0, "")
            .When((_, a) => a.Contains("--verify"), 0, FakeRemote.Sha1 + "\n")
            .When((_, a) => a.Contains("rev-list"), 0, "0 0\n");
        var rig = new Rig(localBranchGuard: new JitLocalBranchGuard(runner)).WithLocalRepo();

        var record = await rig.DeployAsync();

        Assert.AreEqual(BuildStatus.Deployed, record.Status);
    }

    [TestMethod]
    public async Task DeployingAnExplicitCommit_DoesNotRunTheBranchGuard()
    {
        var runner = new RecordingRunner();
        var rig = new Rig(localBranchGuard: new JitLocalBranchGuard(runner)).WithLocalRepo();

        await rig.DeployAsync(new JitDeployOptions(Commit: FakeRemote.Sha2));

        Assert.AreEqual(0, runner.Calls.Count, "an explicit SHA builds that commit rather than the branch's latest work");
    }

    [TestMethod]
    public async Task TheLogIsAlsoStreamedLive_ThroughTheSameEventBus()
    {
        var rig = new Rig();
        var start = await rig.Service.StartDeployAsync("p1", null, null);
        var reader = rig.Bus.Subscribe(start.Record!.Id);
        await start.Completion!;

        var lines = new List<string>();
        while (reader.TryRead(out var msg)) lines.Add(msg);
        Assert.IsTrue(lines.Any(l => l.Contains("LogLine") && l.Contains("[deploy] Building")));
        Assert.IsTrue(lines.Any(l => l.Contains("DeployCompleted") && l.Contains("Deployed")));
    }

    [TestMethod]
    public async Task AFailedDeploy_IsRecordedAsDeployFailed_WithThePlainReason()
    {
        var rig = new Rig();
        rig.Remote.DockerOk = false;
        var rec = await rig.DeployAsync();

        Assert.AreEqual(BuildStatus.DeployFailed, rec.Status);
        StringAssert.Contains(rec.ErrorSummary, "Docker is not available");
        Assert.IsNull(rec.DeployedAt);
    }

    [TestMethod]
    public async Task ARolledBackDeploy_IsAFailureWhoseMessageSaysItWasRolledBack()
    {
        var rig = new Rig();
        await rig.DeployAsync();
        rig.Remote.Branches["master"] = FakeRemote.Sha2;
        rig.Remote.StartFails = (svc, tag) => svc == "api" && tag == "r2-bbbbbbb";

        var rec = await rig.DeployAsync();
        Assert.AreEqual(BuildStatus.DeployFailed, rec.Status);
        StringAssert.Contains(rec.ErrorSummary, "Rolled back");
    }

    [TestMethod]
    public async Task NothingNewToDeploy_IsASuccess_NotAnError()
    {
        var rig = new Rig();
        await rig.DeployAsync();
        var rec = await rig.DeployAsync();

        Assert.AreEqual(BuildStatus.Deployed, rec.Status);
        Assert.IsNull(rec.ErrorSummary);
        StringAssert.Contains(rec.GetLogOutput(), "nothing new to deploy");
    }

    [TestMethod]
    public async Task TheOptionsOfTheRun_ReachTheDeploy()
    {
        var rig = new Rig();
        await rig.DeployAsync(new JitDeployOptions(Commit: FakeRemote.Sha2));
        Assert.IsTrue(rig.Remote.Events.Any(e => e.StartsWith("resolve-commit:bbbbbbb")));
    }

    [TestMethod]
    public async Task TheSettings_ComeFromTheSavedConfig()
    {
        var rig = new Rig();
        rig.ConfigStore.Items["p1"] = rig.ConfigStore.Items["p1"] with { StopGraceSeconds = 77 };
        await rig.DeployAsync();
        Assert.IsTrue(rig.Remote.Commands.Where(c => c.Contains(" stop -t ")).All(c => c.Contains("stop -t 77 ")));
    }

    [TestMethod]
    public async Task AnUnknownProject_IsReported_WithNoRecordCreated()
    {
        var rig = new Rig();
        var start = await rig.Service.StartDeployAsync("nope", null, null);
        StringAssert.Contains(start.Error, "nope");
        Assert.IsNull(start.Record);
        Assert.AreEqual(0, rig.BuildStore.Items.Count);
    }

    [TestMethod]
    public async Task AProjectWithTheModeOff_IsRefused_WithNoRecordAndNoSshCall()
    {
        var rig = new Rig(enabled: false);
        var start = await rig.Service.StartDeployAsync("p1", null, null);
        StringAssert.Contains(start.Error, "not turned on");
        Assert.AreEqual(0, rig.BuildStore.Items.Count);
        Assert.AreEqual(0, rig.Remote.Commands.Count);
    }

    [TestMethod]
    public async Task AnUnexpectedCrashInsideTheDeploy_IsStillRecorded_NeverLeftAsDeploying()
    {
        var rig = new Rig();
        rig.Remote.BeforeCommand = _ => throw new InvalidOperationException("boom");
        var rec = await rig.DeployAsync();
        Assert.AreEqual(BuildStatus.DeployFailed, rec.Status);
        Assert.IsNotNull(rec.ErrorSummary);
    }

    // ═══ recover and roll back ══════════════════════════════════════════════

    [TestMethod]
    public async Task Recover_IsRecordedLikeADeploy()
    {
        var rig = new Rig();
        await rig.DeployAsync();
        rig.Remote.Branches["master"] = FakeRemote.Sha2;
        rig.Remote.ThrowWhen = c => c.Contains(" stop -t ") && c.Contains("'api'");
        await rig.DeployAsync();               // connection lost mid-deploy
        rig.Remote.ThrowWhen = null;

        var start = await rig.Service.StartRecoverAsync("p1", "tester");
        await start.Completion!;
        var rec = (await rig.BuildStore.GetByIdAsync(start.Record!.Id))!;

        Assert.AreEqual(BuildStatus.DeployFailed, rec.Status, "an undone deploy is not a success");
        StringAssert.Contains(rec.ErrorSummary, "Rolled back");
        Assert.AreEqual(DeployPhase.Idle, rig.Remote.ReadState().Phase);
    }

    [TestMethod]
    public async Task ARollbackOnPurpose_IsMarkedAsARollback_AndGoesBackToTheChosenRelease()
    {
        var rig = new Rig();
        await rig.DeployAsync();
        rig.Remote.Branches["master"] = FakeRemote.Sha2;
        await rig.DeployAsync();

        var start = await rig.Service.StartRollbackAsync("p1", "r1-aaaaaaa", "tester");
        Assert.IsNull(start.Error, start.Error);
        await start.Completion!;
        var rec = (await rig.BuildStore.GetByIdAsync(start.Record!.Id))!;

        Assert.AreEqual(BuildStatus.Deployed, rec.Status);
        Assert.IsTrue(rec.IsRollback);
        Assert.AreEqual("r1-aaaaaaa", rec.GitTag);
        Assert.AreEqual("r1-aaaaaaa", rig.Remote.ReadState().CurrentTag);
    }

    [TestMethod]
    public async Task ARollbackToSomethingThatIsNotAReleaseTag_IsRefusedUpFront()
    {
        var rig = new Rig();
        var start = await rig.Service.StartRollbackAsync("p1", "latest; id", null);
        Assert.IsNotNull(start.Error);
        Assert.AreEqual(0, rig.BuildStore.Items.Count);
    }

    // ═══ the releases view ══════════════════════════════════════════════════

    [TestMethod]
    public async Task Releases_ShowsWhatIsKept_NewestFirst_WithCurrentAndPreviousMarked()
    {
        var rig = new Rig();
        await rig.DeployAsync();
        rig.Remote.Branches["master"] = FakeRemote.Sha2;
        await rig.DeployAsync();

        var v = await rig.Service.GetReleasesAsync("p1");
        Assert.IsNull(v.Error);
        Assert.AreEqual("Idle", v.Phase);
        Assert.AreEqual("r2-bbbbbbb", v.CurrentTag);
        Assert.AreEqual("r1-aaaaaaa", v.PreviousTag);
        Assert.IsFalse(v.NeedsAttention);
        CollectionAssert.AreEqual(new[] { "r2-bbbbbbb", "r1-aaaaaaa" }, v.Releases.Select(r => r.Tag).ToArray());
        Assert.IsTrue(v.Releases[0].IsCurrent && v.Releases[1].IsPrevious);
        Assert.AreEqual(FakeRemote.Sha2, v.Releases[0].Sha);
    }

    [TestMethod]
    public async Task Releases_OfAServerWithNoneYet_IsEmpty()
    {
        var v = await new Rig().Service.GetReleasesAsync("p1");
        Assert.IsNull(v.Error);
        Assert.AreEqual(0, v.Releases.Count);
        Assert.IsNull(v.CurrentTag);
    }

    [TestMethod]
    public async Task Releases_FlagsAServerThatNeedsAttention()
    {
        var rig = new Rig();
        await rig.DeployAsync();
        rig.Remote.Branches["master"] = FakeRemote.Sha2;
        rig.Remote.StartFails = (svc, _) => svc == "api";
        await rig.DeployAsync();                                     // new fails, rollback fails
        var v = await rig.Service.GetReleasesAsync("p1");

        Assert.IsTrue(v.NeedsAttention);
        Assert.AreEqual("RollbackFailed", v.Phase);
        StringAssert.Contains(v.Attention, "recover");
    }

    [TestMethod]
    public async Task Releases_WhenTheServerCannotBeReached_SaysSo_InsteadOfThrowing()
    {
        var rig = new Rig();
        rig.Remote.BeforeCommand = _ => throw new IOException("no route to host");
        var v = await rig.Service.GetReleasesAsync("p1");
        StringAssert.Contains(v.Error, "no route to host");
    }

    [TestMethod]
    public async Task Releases_WhenTheStateFileIsDamaged_SaysSo()
    {
        var rig = new Rig();
        rig.Remote.StateJson = "{ nope";
        var v = await rig.Service.GetReleasesAsync("p1");
        StringAssert.Contains(v.Error!.ToLowerInvariant(), "state");
    }

    // ═══ routes ═════════════════════════════════════════════════════════════

    private static int Status(IResult r) => ((IStatusCodeHttpResult)r).StatusCode ?? 0;
    private static object? Value(IResult r) => ((IValueHttpResult)r).Value;

    [TestMethod]
    public async Task Route_GetConfig_ReturnsTheDefaultsForAProjectWithNone_PlusWhatComposeMustLookLike()
    {
        var rig = new Rig();
        rig.ConfigStore.Items.Remove("p1");
        var r = await JitDeployRoutes.HandleGetConfigAsync("p1", rig.ProjectStore, rig.ConfigStore);

        Assert.AreEqual(200, Status(r));
        var v = Value(r)!;
        var json = System.Text.Json.JsonSerializer.Serialize(v, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        StringAssert.Contains(json, "\"enabled\":false");
        StringAssert.Contains(json, "API_TAG");                       // the tag variable the compose file must use
        StringAssert.Contains(json, "${API_TAG:-shop/api:latest}");   // spelled out for a novice
    }

    [TestMethod]
    public async Task Route_GetConfig_ForAnUnknownProject_IsNotFound()
    {
        var rig = new Rig();
        Assert.AreEqual(404, Status(await JitDeployRoutes.HandleGetConfigAsync("nope", rig.ProjectStore, rig.ConfigStore)));
    }

    [TestMethod]
    public async Task Route_PutConfig_SavesAValidConfig()
    {
        var rig = new Rig();
        var r = await JitDeployRoutes.HandleSaveConfigAsync("p1", new JitDeployConfig { Enabled = true, RepoUrl = "git@h:o/r.git", Branch = "main" }, rig.ProjectStore, rig.ConfigStore);

        Assert.AreEqual(200, Status(r));
        Assert.AreEqual("main", rig.ConfigStore.Items["p1"].Branch);
    }

    [TestMethod]
    public async Task Route_PutConfig_RefusesAnInvalidConfig_AndSavesNothing()
    {
        var rig = new Rig();
        var before = rig.ConfigStore.Items["p1"];
        var r = await JitDeployRoutes.HandleSaveConfigAsync("p1", new JitDeployConfig { Enabled = true, RepoUrl = "git@h:o/r.git", Branch = "--delete" }, rig.ProjectStore, rig.ConfigStore);

        Assert.AreEqual(400, Status(r));
        Assert.AreSame(before, rig.ConfigStore.Items["p1"]);
    }

    [TestMethod]
    public async Task Route_Deploy_Accepts_AndReturnsTheBuildId()
    {
        var rig = new Rig();
        var r = await JitDeployRoutes.HandleDeployAsync("p1", new JitDeployOptions(), "tester", rig.Service);

        Assert.AreEqual(202, Status(r));
        StringAssert.Contains(System.Text.Json.JsonSerializer.Serialize(Value(r), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)), "buildId");
    }

    [TestMethod]
    public async Task Route_Deploy_ForAnUnknownProject_Or_ModeOff_IsABadRequest()
    {
        var rig = new Rig(enabled: false);
        Assert.AreEqual(400, Status(await JitDeployRoutes.HandleDeployAsync("p1", new JitDeployOptions(), null, rig.Service)));
        Assert.AreEqual(404, Status(await JitDeployRoutes.HandleDeployAsync("nope", new JitDeployOptions(), null, rig.Service)));
    }

    [TestMethod]
    public async Task Route_Rollback_NeedsATag()
    {
        var rig = new Rig();
        Assert.AreEqual(400, Status(await JitDeployRoutes.HandleRollbackAsync("p1", new RollbackRequest(null), null, rig.Service)));
        Assert.AreEqual(400, Status(await JitDeployRoutes.HandleRollbackAsync("p1", new RollbackRequest(" "), null, rig.Service)));
    }

    [TestMethod]
    public async Task Route_Releases_ReturnsTheView()
    {
        var rig = new Rig();
        var r = await JitDeployRoutes.HandleReleasesAsync("p1", rig.Service);
        Assert.AreEqual(200, Status(r));
        Assert.IsInstanceOfType(Value(r), typeof(JitReleasesView));
    }
}
