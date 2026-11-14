using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.JitDeploy;

namespace ShipRight.Tests.Modules.JitDeploy;

/// <summary>
/// The whole deploy, end to end, against a pretend server. Every row of the design's failure table is here:
/// a failure before the old release is touched changes nothing; a failure after it rolls back immediately;
/// a failed rollback is reported, never looped; and an interrupted deploy is recovered from the saved state.
/// </summary>
[TestClass]
public class JitDeployerTests
{
    private static readonly JitTarget Target = new("h", "u", "/k", "/home/deploy/shop");

    private static JitDeployRequest Req() => new()
    {
        Target = Target,
        Project = "shop",
        RepoUrl = "git@github.com:o/r.git",
        Branch = "master",
        Services = [new JitService("api", "API_TAG", "shop/api"), new JitService("web", "WEB_TAG", "shop/web")],
    };

    private sealed class Rig
    {
        public FakeRemote Remote { get; } = new();
        public FakeClock Clock { get; } = new();
        public JitDeployLock Lock { get; } = new();
        public List<string> Lines { get; } = [];
        public JitDeployer Deployer => new(Remote, Clock, Lock);

        public Task<JitDeployResult> Deploy(JitDeployRequest? req = null, CancellationToken ct = default) =>
            Deployer.DeployAsync(req ?? Req(), l => { lock (Lines) Lines.Add(l); return Task.CompletedTask; }, ct);

        public Task<JitDeployResult> Recover(JitDeployRequest? req = null) =>
            Deployer.RecoverAsync(req ?? Req(), l => { lock (Lines) Lines.Add(l); return Task.CompletedTask; });

        /// <summary>A first, healthy release, with the logs cleared so a test sees only what it is about.</summary>
        public async Task SeedAsync()
        {
            var r = await Deploy();
            Assert.AreEqual(JitOutcome.Deployed, r.Outcome, r.Message);
            Remote.Events.Clear(); Remote.Writes.Clear(); Remote.Commands.Clear(); Lines.Clear();
            Remote.Branches["master"] = FakeRemote.Sha2;
        }

        public string[] Core => Remote.Events.Where(e => e.StartsWith("build:") || e.StartsWith("stop:") || e.StartsWith("start:") || e.StartsWith("checkout:")).ToArray();
    }

    private static void SeqEq(string[] expected, string[] actual) =>
        CollectionAssert.AreEqual(expected, actual, "actual: " + string.Join(" | ", actual));

    // ═══ the happy path ═════════════════════════════════════════════════════

    [TestMethod]
    public async Task AFirstDeploy_BuildsEverything_ThenStopsInReverseAndStartsInDependencyOrder()
    {
        var rig = new Rig();
        var r = await rig.Deploy();

        Assert.AreEqual(JitOutcome.Deployed, r.Outcome, r.Message);
        Assert.AreEqual("r1-aaaaaaa", r.Tag);
        Assert.AreEqual(FakeRemote.Sha1, r.Sha);
        SeqEq(["checkout:aaaaaaa", "build:api,web", "stop:web", "stop:api", "start:api@r1-aaaaaaa", "start:web@r1-aaaaaaa"], rig.Core);
        Assert.IsFalse(rig.Remote.Events.Contains("stop:db"), "a service that was not replaced is never touched");
        Assert.AreEqual("shop/web:r1-aaaaaaa", rig.Remote.Running["web"]);
    }

    [TestMethod]
    public async Task Preflight_RunsBeforeAnythingElse()
    {
        var rig = new Rig();
        await rig.Deploy();
        var e = rig.Remote.Events;
        Assert.IsTrue(e.IndexOf("docker-check") < e.IndexOf("sync") && e.IndexOf("disk-check") < e.IndexOf("sync") && e.IndexOf("git-check") < e.IndexOf("sync"));
    }

    [TestMethod]
    public async Task TheSavedState_WalksThroughEveryPhase_AndEndsIdleWithTheNewReleaseCurrent()
    {
        var rig = new Rig();
        await rig.Deploy();

        CollectionAssert.AreEqual(new[]
        {
            DeployPhase.Syncing, DeployPhase.Building, DeployPhase.OldStopped, DeployPhase.NewStarted, DeployPhase.Verified, DeployPhase.Idle,
        }, rig.Remote.Writes);
        var s = rig.Remote.ReadState();
        Assert.AreEqual("r1-aaaaaaa", s.CurrentTag);
        Assert.IsNull(s.PreviousTag);
        Assert.IsNull(s.InProgressTag);
        var rel = s.Releases.Single();
        Assert.AreEqual(FakeRemote.Sha1, rel.Sha);
        CollectionAssert.AreEquivalent(new[] { "shop/api:r1-aaaaaaa", "shop/web:r1-aaaaaaa" }, rel.Images.ToArray());
    }

    [TestMethod]
    public async Task ASecondDeploy_GetsTheNextNumber_AndRemembersThePreviousRelease()
    {
        var rig = new Rig();
        await rig.SeedAsync();
        var r = await rig.Deploy();

        Assert.AreEqual(JitOutcome.Deployed, r.Outcome, r.Message);
        Assert.AreEqual("r2-bbbbbbb", r.Tag);
        var s = rig.Remote.ReadState();
        Assert.AreEqual("r2-bbbbbbb", s.CurrentTag);
        Assert.AreEqual("r1-aaaaaaa", s.PreviousTag);
        Assert.AreEqual(2, s.Releases.Count);
        Assert.IsTrue(rig.Remote.Images.Contains("shop/api:r1-aaaaaaa"), "the previous release's images are kept for rollback");
    }

    [TestMethod]
    public async Task WhenTheBranchHasNotMoved_ThereIsNothingToDeploy()
    {
        var rig = new Rig();
        await rig.SeedAsync();
        rig.Remote.Branches["master"] = FakeRemote.Sha1;
        var r = await rig.Deploy();

        Assert.AreEqual(JitOutcome.NothingToDeploy, r.Outcome);
        Assert.IsTrue(r.Success);
        Assert.AreEqual(0, rig.Core.Count(e => e.StartsWith("build:") || e.StartsWith("stop:")));
        Assert.AreEqual(DeployPhase.Idle, rig.Remote.ReadState().Phase);
    }

    [TestMethod]
    public async Task Force_DeploysEvenWhenTheBranchHasNotMoved()
    {
        var rig = new Rig();
        await rig.SeedAsync();
        rig.Remote.Branches["master"] = FakeRemote.Sha1;
        var r = await rig.Deploy(Req() with { Force = true });

        Assert.AreEqual(JitOutcome.Deployed, r.Outcome, r.Message);
        Assert.AreEqual("r2-aaaaaaa", r.Tag);
    }

    [TestMethod]
    public async Task ASpecificCommit_IsDeployedInsteadOfTheBranchHead()
    {
        var rig = new Rig();
        var r = await rig.Deploy(Req() with { Commit = FakeRemote.Sha2 });

        Assert.AreEqual(JitOutcome.Deployed, r.Outcome, r.Message);
        Assert.AreEqual("r1-bbbbbbb", r.Tag);
        Assert.IsFalse(rig.Remote.Events.Any(e => e.StartsWith("resolve:")));
        Assert.IsTrue(rig.Remote.Events.Any(e => e.StartsWith("resolve-commit:bbbbbbb")));
    }

    [TestMethod]
    public async Task TheHealthGate_WaitsForAServiceThatIsStillStarting()
    {
        var rig = new Rig();
        rig.Remote.Probe = (_, _, n) => n < 3 ? "running|starting|0" : "running|healthy|0";
        var r = await rig.Deploy();

        Assert.AreEqual(JitOutcome.Deployed, r.Outcome, r.Message);
        Assert.IsTrue(rig.Clock.Delays >= 3);
    }

    [TestMethod]
    public async Task TheLog_NarratesEveryStep_SoNobodyHasToGuess()
    {
        var rig = new Rig();
        await rig.Deploy();
        var all = string.Join("\n", rig.Lines);
        foreach (var expected in new[] { "[deploy] Checking the server", "[deploy] Fetching", "r1-aaaaaaa", "[deploy] Building", "[deploy] Stopping web", "[deploy] Starting api", "healthy", "[deploy] Release r1-aaaaaaa is live" })
            StringAssert.Contains(all, expected);
        StringAssert.Contains(all, "#1 building...", "the build's own output is streamed");
    }

    // ═══ dependents ═════════════════════════════════════════════════════════

    private static void AddProxy(FakeRemote r) => r.Stack.Add(new("proxy", ["web"], false, "nginx:1", null));

    [TestMethod]
    public async Task ADependentThatIsNotBuilt_IsRestartedByDefault_InTheRightOrder()
    {
        var rig = new Rig();
        AddProxy(rig.Remote);
        await rig.Deploy();
        SeqEq(["checkout:aaaaaaa", "build:api,web", "stop:proxy", "stop:web", "stop:api", "start:api@r1-aaaaaaa", "start:web@r1-aaaaaaa", "start:proxy@1"], rig.Core);
    }

    [TestMethod]
    public async Task ALeaveRunningChoice_KeepsThatDependentUntouched()
    {
        var rig = new Rig();
        AddProxy(rig.Remote);
        var req = Req() with { DependentChoices = new Dictionary<(string, string), bool> { [("web", "proxy")] = false } };
        await rig.Deploy(req);
        Assert.IsFalse(rig.Remote.Events.Any(e => e.EndsWith(":proxy") || e.StartsWith("start:proxy")));
    }

    // ═══ failures BEFORE the old release is touched ═════════════════════════

    private static void AssertUntouched(Rig rig, string[]? writes = null)
    {
        Assert.IsFalse(rig.Remote.Events.Any(e => e.StartsWith("stop:") || e.StartsWith("start:")), "nothing may be stopped or started");
        if (writes is not null) CollectionAssert.AreEqual(writes, rig.Remote.Writes.Select(w => w.ToString()).ToArray());
    }

    [TestMethod]
    public async Task DockerMissing_StopsBeforeAnyChange_WithAPlainMessage()
    {
        var rig = new Rig();
        rig.Remote.DockerOk = false;
        var r = await rig.Deploy();

        Assert.AreEqual(JitOutcome.FailedBeforeChange, r.Outcome);
        StringAssert.Contains(r.Message, "Docker is not available");
        AssertUntouched(rig, []);
        Assert.IsNull(rig.Remote.StateJson, "not even a state file is created");
    }

    [TestMethod]
    public async Task NotEnoughDisk_StopsBeforeAnyChange()
    {
        var rig = new Rig();
        rig.Remote.FreeMb = 100;
        var r = await rig.Deploy();
        Assert.AreEqual(JitOutcome.FailedBeforeChange, r.Outcome);
        StringAssert.Contains(r.Message, "Not enough free disk space");
        AssertUntouched(rig, []);
    }

    [TestMethod]
    public async Task AnUnreachableRepository_StopsBeforeAnyChange()
    {
        var rig = new Rig();
        rig.Remote.GitOk = false;
        var r = await rig.Deploy();
        Assert.AreEqual(JitOutcome.FailedBeforeChange, r.Outcome);
        StringAssert.Contains(r.Message, "Cannot reach the git repository");
        AssertUntouched(rig, []);
    }

    [TestMethod]
    public async Task ABranchThatDoesNotExist_IsNamedInTheMessage_AndLeavesTheStateIdle()
    {
        var rig = new Rig();
        var r = await rig.Deploy(Req() with { Branch = "nope" });
        Assert.AreEqual(JitOutcome.FailedBeforeChange, r.Outcome);
        StringAssert.Contains(r.Message, "nope");
        AssertUntouched(rig, ["Syncing", "Idle"]);
    }

    [TestMethod]
    public async Task AFailedGitSync_LeavesEverythingAsItWas()
    {
        var rig = new Rig();
        rig.Remote.SyncOk = false;
        var r = await rig.Deploy();
        Assert.AreEqual(JitOutcome.FailedBeforeChange, r.Outcome);
        AssertUntouched(rig, ["Syncing", "Idle"]);
    }

    [TestMethod]
    public async Task ADependencyCycle_IsRefusedBeforeBuilding_InPlainLanguage()
    {
        var rig = new Rig();
        rig.Remote.ComposeJsonFor = _ => """{ "services": { "api": {"build": ".", "image": "x", "depends_on": {"web": {}}}, "web": {"build": ".", "image": "y", "depends_on": {"api": {}}} } }""";
        var r = await rig.Deploy();
        Assert.AreEqual(JitOutcome.FailedBeforeChange, r.Outcome);
        StringAssert.Contains(r.Message, "circle");
        Assert.IsFalse(rig.Remote.Events.Any(e => e.StartsWith("build:")));
        AssertUntouched(rig, ["Syncing", "Idle"]);
    }

    [TestMethod]
    public async Task AnImageLineThatIgnoresTheTagVariable_IsExplained_AndNothingIsBuilt()
    {
        var rig = new Rig();
        rig.Remote.ComposeJsonFor = _ => """{ "services": { "api": {"build": ".", "image": "shop/api:latest"}, "web": {"build": ".", "image": "shop/web:latest"} } }""";
        var r = await rig.Deploy();
        Assert.AreEqual(JitOutcome.FailedBeforeChange, r.Outcome);
        StringAssert.Contains(r.Message, "${API_TAG");
        StringAssert.Contains(r.Message, "image:");
        Assert.IsFalse(rig.Remote.Events.Any(e => e.StartsWith("build:")));
    }

    [TestMethod]
    public async Task AFailedBuild_NeverTouchesTheRunningRelease_AndCleansUpAfterItself()
    {
        var rig = new Rig();
        await rig.SeedAsync();
        rig.Remote.BuildOk = false;
        var r = await rig.Deploy();

        Assert.AreEqual(JitOutcome.FailedBeforeChange, r.Outcome);
        StringAssert.Contains(r.Message, "build");
        StringAssert.Contains(r.Message, "failed to solve");
        AssertUntouched(rig, ["Syncing", "Building", "FailedBuild", "Idle"]);
        Assert.AreEqual("shop/web:r1-aaaaaaa", rig.Remote.Running["web"], "the old release is still running");
        Assert.AreEqual("r1-aaaaaaa", rig.Remote.ReadState().CurrentTag);
        Assert.IsTrue(rig.Remote.Commands.Any(c => c.Contains("docker image rm") && c.Contains("r2-bbbbbbb")), "partial images are removed");
    }

    [TestMethod]
    public async Task ABuildThatReportsSuccessButLeavesNoImages_IsTreatedAsFailed()
    {
        var rig = new Rig();
        rig.Remote.KeepImagesAfterBuild = false;
        var r = await rig.Deploy();
        Assert.AreEqual(JitOutcome.FailedBeforeChange, r.Outcome);
        AssertUntouched(rig, ["Syncing", "Building", "FailedBuild", "Idle"]);
    }

    [TestMethod]
    public async Task WhenThePreviousReleasesImagesAreGone_ItRefusesUnlessToldItIsFine()
    {
        var rig = new Rig();
        await rig.SeedAsync();
        rig.Remote.Images.Remove("shop/api:r1-aaaaaaa");

        var refused = await rig.Deploy();
        Assert.AreEqual(JitOutcome.FailedBeforeChange, refused.Outcome);
        StringAssert.Contains(refused.Message, "rollback");
        AssertUntouched(rig);

        var allowed = await rig.Deploy(Req() with { AllowWithoutRollback = true });
        Assert.AreEqual(JitOutcome.Deployed, allowed.Outcome, allowed.Message);
    }

    [TestMethod]
    public async Task AnUnsafeRequest_IsRefusedBeforeAnythingIsSent()
    {
        var rig = new Rig();
        var r = await rig.Deploy(Req() with { Branch = "a;rm -rf /" });
        Assert.AreEqual(JitOutcome.FailedBeforeChange, r.Outcome);
        Assert.AreEqual(0, rig.Remote.Commands.Count);
    }

    [TestMethod]
    public async Task ARequestWithNoServices_IsRefused()
    {
        var rig = new Rig();
        var r = await rig.Deploy(Req() with { Services = [] });
        Assert.AreEqual(JitOutcome.FailedBeforeChange, r.Outcome);
        Assert.AreEqual(0, rig.Remote.Commands.Count);
    }

    // ═══ failures AFTER the old release was stopped: roll back at once ═════

    private static async Task<Rig> SeededRigAsync()
    {
        var rig = new Rig();
        await rig.SeedAsync();
        return rig;
    }

    private static void AssertRolledBack(Rig rig, JitDeployResult r)
    {
        Assert.AreEqual(JitOutcome.RolledBack, r.Outcome, r.Message);
        StringAssert.Contains(r.Message, "r1-aaaaaaa");
        var s = rig.Remote.ReadState();
        Assert.AreEqual(DeployPhase.Idle, s.Phase);
        Assert.AreEqual("r1-aaaaaaa", s.CurrentTag);
        Assert.IsNull(s.InProgressTag);
        Assert.AreEqual(1, s.Releases.Count, "the failed release is forgotten");
        Assert.AreEqual("shop/api:r1-aaaaaaa", rig.Remote.Running["api"]);
        Assert.AreEqual("shop/web:r1-aaaaaaa", rig.Remote.Running["web"]);
        Assert.IsFalse(rig.Remote.Images.Contains("shop/api:r2-bbbbbbb"), "the failed release's images are removed");
        Assert.IsTrue(rig.Remote.Events.Contains("checkout:aaaaaaa"), "the old release's own compose file is restored");
    }

    [TestMethod]
    public async Task AStopThatFails_RollsBackToThePreviousRelease()
    {
        var rig = await SeededRigAsync();
        rig.Remote.StopFails = svc => svc == "api";
        var r = await rig.Deploy();

        AssertRolledBack(rig, r);
        CollectionAssert.IsSubsetOf(new[] { DeployPhase.OldStopped, DeployPhase.RollingBack, DeployPhase.RolledBack, DeployPhase.Idle }, rig.Remote.Writes);
        Assert.IsFalse(rig.Remote.Writes.Contains(DeployPhase.NewStarted), "nothing new was started");
    }

    [TestMethod]
    public async Task ANewServiceThatFailsToStart_RollsBack()
    {
        var rig = await SeededRigAsync();
        rig.Remote.StartFails = (svc, tag) => svc == "api" && tag == "r2-bbbbbbb";
        var r = await rig.Deploy();

        AssertRolledBack(rig, r);
        Assert.AreEqual("api", r.FailedService);
        CollectionAssert.IsSubsetOf(new[] { DeployPhase.NewStarted, DeployPhase.RollingBack }, rig.Remote.Writes);
    }

    [TestMethod]
    public async Task AnUnhealthyNewService_RollsBack_NamingTheServiceAndTheReason()
    {
        var rig = await SeededRigAsync();
        rig.Remote.Probe = (svc, tag, _) => svc == "api" && tag == "r2-bbbbbbb" ? "running|unhealthy|0" : "running|healthy|0";
        var r = await rig.Deploy();

        AssertRolledBack(rig, r);
        Assert.AreEqual("api", r.FailedService);
        StringAssert.Contains(r.Message, "unhealthy");
        Assert.IsFalse(rig.Remote.Events.Contains("start:web@r2-bbbbbbb"), "services after the failure are never started");
    }

    [TestMethod]
    public async Task ACrashLoopingNewService_RollsBack()
    {
        var rig = await SeededRigAsync();
        // no health check: the gate watches the service for the settle time, and the restart counter rises
        rig.Remote.Probe = (svc, tag, n) => svc == "web" && tag == "r2-bbbbbbb" ? (n >= 1 ? "running||2" : "running||0") : "running|healthy|0";
        var r = await rig.Deploy();

        AssertRolledBack(rig, r);
        Assert.AreEqual("web", r.FailedService);
        StringAssert.Contains(r.Message, "restart");
    }

    [TestMethod]
    public async Task AServiceThatNeverBecomesHealthy_RollsBackAtTheTimeout()
    {
        var rig = await SeededRigAsync();
        rig.Remote.Probe = (svc, tag, _) => svc == "api" && tag == "r2-bbbbbbb" ? "running|starting|0" : "running|healthy|0";
        var before = rig.Clock.UtcNow;
        var r = await rig.Deploy();

        AssertRolledBack(rig, r);
        StringAssert.Contains(r.Message, "60 seconds");
        Assert.IsTrue(rig.Clock.UtcNow - before >= TimeSpan.FromSeconds(60));
    }

    [TestMethod]
    public async Task ATunedHealthTimeout_IsHonoured()
    {
        var rig = await SeededRigAsync();
        rig.Remote.Probe = (svc, tag, _) => svc == "api" && tag == "r2-bbbbbbb" ? "running|starting|0" : "running|healthy|0";
        var r = await rig.Deploy(Req() with { Settings = new JitDeploySettings { HealthTimeoutSeconds = 20 } });

        StringAssert.Contains(r.Message, "20 seconds");
    }

    [TestMethod]
    public async Task ARollbackThatFailsOnce_IsRetriedOnce_ThenSucceeds()
    {
        var rig = await SeededRigAsync();
        var oldApiStarts = 0;
        rig.Remote.StartFails = (svc, tag) =>
        {
            if (svc == "api" && tag == "r2-bbbbbbb") return true;
            if (svc == "api" && tag == "r1-aaaaaaa") return ++oldApiStarts == 1;
            return false;
        };
        var r = await rig.Deploy();

        AssertRolledBack(rig, r);
        Assert.AreEqual(2, rig.Remote.Events.Count(e => e == "start:api@r1-aaaaaaa"));
    }

    [TestMethod]
    public async Task ARollbackThatFailsTwice_IsReportedWithTheManualCommands_AndNeverLoops()
    {
        var rig = await SeededRigAsync();
        rig.Remote.StartFails = (svc, tag) => svc == "api";      // the new AND the old release's api both fail
        var r = await rig.Deploy();

        Assert.AreEqual(JitOutcome.RollbackFailed, r.Outcome);
        StringAssert.Contains(r.Message, "r1-aaaaaaa");
        StringAssert.Contains(r.Message, "docker compose");
        StringAssert.Contains(r.Message, "git");
        Assert.AreEqual(2, rig.Remote.Events.Count(e => e == "start:api@r1-aaaaaaa"), "one retry, no more");
        Assert.AreEqual(DeployPhase.RollbackFailed, rig.Remote.ReadState().Phase);
    }

    [TestMethod]
    public async Task AfterAFailedRollback_NewDeploysAreBlocked_UntilTheRollbackIsRetriedAndWorks()
    {
        var rig = await SeededRigAsync();
        rig.Remote.StartFails = (svc, _) => svc == "api";
        await rig.Deploy();
        rig.Remote.Events.Clear();

        var blocked = await rig.Deploy();
        Assert.AreEqual(JitOutcome.Blocked, blocked.Outcome);
        Assert.IsFalse(rig.Remote.Events.Any(e => e.StartsWith("build:") || e.StartsWith("stop:")));

        rig.Remote.StartFails = (_, _) => false;               // the operator fixed the cause
        var recovered = await rig.Recover();
        Assert.AreEqual(JitOutcome.RolledBack, recovered.Outcome, recovered.Message);
        Assert.AreEqual(DeployPhase.Idle, rig.Remote.ReadState().Phase);
        Assert.AreEqual("shop/api:r1-aaaaaaa", rig.Remote.Running["api"]);
    }

    [TestMethod]
    public async Task AFirstDeployThatFails_StopsWhatStarted_AndSaysThereWasNothingToRestore()
    {
        var rig = new Rig();
        rig.Remote.StartFails = (svc, _) => svc == "web";
        var r = await rig.Deploy();

        Assert.AreEqual(JitOutcome.FailedNoPreviousRelease, r.Outcome);
        StringAssert.Contains(r.Message.ToLowerInvariant(), "no previous release");
        Assert.AreEqual(0, rig.Remote.Running.Count, "what had started is stopped again");
        var s = rig.Remote.ReadState();
        Assert.AreEqual(DeployPhase.Idle, s.Phase);
        Assert.IsNull(s.CurrentTag);
        Assert.AreEqual(0, s.Releases.Count);
        CollectionAssert.IsSubsetOf(new[] { DeployPhase.OldStopped, DeployPhase.NewStarted, DeployPhase.RollingBack, DeployPhase.RolledBack, DeployPhase.Idle }, rig.Remote.Writes);
        Assert.IsFalse(rig.Remote.Images.Any(i => i.Contains("r1-aaaaaaa")));
    }

    // ═══ interruption and recovery ══════════════════════════════════════════

    [TestMethod]
    public async Task ALostConnection_IsReportedAsUnknown_AndTheSavedStateRecordsWhereItStopped()
    {
        var rig = await SeededRigAsync();
        rig.Remote.ThrowWhen = c => c.Contains(" stop -t ") && c.Contains("'api'");
        var r = await rig.Deploy();

        Assert.AreEqual(JitOutcome.ConnectionLost, r.Outcome);
        StringAssert.Contains(r.Message, "recover");
        Assert.AreEqual(DeployPhase.OldStopped, rig.Remote.ReadState().Phase);
    }

    [TestMethod]
    public async Task RecoverAfterACrash_RollsBackToThePreviousRelease()
    {
        var rig = await SeededRigAsync();
        rig.Remote.ThrowWhen = c => c.Contains(" stop -t ") && c.Contains("'api'");
        await rig.Deploy();
        rig.Remote.ThrowWhen = null;

        var r = await rig.Recover();   // a brand-new deployer, as after a restart
        AssertRolledBack(rig, r);
    }

    [TestMethod]
    public async Task ANewDeploy_FirstRecoversAnInterruptedOne_ThenStops_SoTheUserDecides()
    {
        var rig = await SeededRigAsync();
        rig.Remote.ThrowWhen = c => c.Contains("up -d --no-deps") && c.Contains("'web'");
        await rig.Deploy();
        rig.Remote.ThrowWhen = null;
        rig.Remote.Events.Clear();

        var r = await rig.Deploy();
        Assert.AreEqual(JitOutcome.RolledBack, r.Outcome, r.Message);
        StringAssert.Contains(r.Message, "interrupted");
        Assert.IsFalse(rig.Remote.Events.Any(e => e.StartsWith("build:")), "it does not start a new deploy on top of the recovery");
    }

    [TestMethod]
    public async Task ACrashRightAfterVerification_IsCommittedOnRecovery()
    {
        var rig = await SeededRigAsync();
        rig.Remote.StateJson = ReleaseStateJson.Serialize(rig.Remote.ReadState() with
        {
            Phase = DeployPhase.Verified,
            InProgressTag = "r2-bbbbbbb",
            Releases =
            [
                .. rig.Remote.ReadState().Releases,
                new ReleaseRecord("r2-bbbbbbb", FakeRemote.Sha2, ["shop/api:r2-bbbbbbb", "shop/web:r2-bbbbbbb"], rig.Clock.UtcNow),
            ],
        });
        foreach (var i in new[] { "shop/api:r2-bbbbbbb", "shop/web:r2-bbbbbbb" }) rig.Remote.Images.Add(i);

        var r = await rig.Recover();
        Assert.AreEqual(JitOutcome.Deployed, r.Outcome, r.Message);
        var s = rig.Remote.ReadState();
        Assert.AreEqual(DeployPhase.Idle, s.Phase);
        Assert.AreEqual("r2-bbbbbbb", s.CurrentTag);
        Assert.AreEqual("r1-aaaaaaa", s.PreviousTag);
    }

    [TestMethod]
    public async Task ADeployThatDiedWhileBuilding_IsResetAndThenDeploysNormally()
    {
        var rig = new Rig();
        rig.Remote.StateJson = ReleaseStateJson.Serialize(new ReleaseState { Phase = DeployPhase.Building, InProgressTag = "r1-aaaaaaa" });
        var r = await rig.Deploy();
        Assert.AreEqual(JitOutcome.Deployed, r.Outcome, r.Message);
        Assert.AreEqual("r1-aaaaaaa", rig.Remote.ReadState().CurrentTag);
    }

    [TestMethod]
    public async Task RecoverOnAHealthyHost_HasNothingToDo()
    {
        var rig = new Rig();
        var r = await rig.Recover();
        Assert.AreEqual(JitOutcome.NothingToDeploy, r.Outcome);
        Assert.AreEqual(0, rig.Remote.Events.Count(e => e.StartsWith("stop:") || e.StartsWith("start:")));
    }

    [TestMethod]
    public async Task AnUnreadableStateFile_BlocksTheDeploy_InsteadOfBeingOverwritten()
    {
        var rig = new Rig();
        rig.Remote.StateJson = "{ not json";
        var r = await rig.Deploy();
        Assert.AreEqual(JitOutcome.Blocked, r.Outcome);
        StringAssert.Contains(r.Message, "state");
        Assert.AreEqual("{ not json", rig.Remote.StateJson);
        Assert.AreEqual(0, rig.Remote.Writes.Count);
    }

    // ═══ cancellation ═══════════════════════════════════════════════════════

    [TestMethod]
    public async Task CancellingDuringTheBuild_ChangesNothing()
    {
        var rig = await SeededRigAsync();
        using var cts = new CancellationTokenSource();
        rig.Remote.BeforeCommand = c => { if (c.Contains("build --progress")) cts.Cancel(); };
        var r = await rig.Deploy(ct: cts.Token);

        Assert.AreEqual(JitOutcome.FailedBeforeChange, r.Outcome);
        StringAssert.Contains(r.Message.ToLowerInvariant(), "cancel");
        AssertUntouched(rig);
        Assert.AreEqual(DeployPhase.Idle, rig.Remote.ReadState().Phase);
        Assert.AreEqual("shop/web:r1-aaaaaaa", rig.Remote.Running["web"]);
    }

    [TestMethod]
    public async Task CancellingAfterTheOldReleaseWasStopped_StillRollsBack()
    {
        var rig = await SeededRigAsync();
        using var cts = new CancellationTokenSource();
        rig.Remote.BeforeCommand = c => { if (c.Contains("up -d --no-deps") && c.Contains("r2-bbbbbbb") && c.Contains("'web'")) cts.Cancel(); };
        var r = await rig.Deploy(ct: cts.Token);

        AssertRolledBack(rig, r);
    }

    // ═══ rolling back on purpose, to any retained release ═══════════════════

    private static async Task<Rig> TwoReleasesAsync()
    {
        var rig = new Rig();
        await rig.SeedAsync();                       // r1 live; branch now points at sha2
        var r2 = await rig.Deploy();                 // r2 live, r1 kept
        Assert.AreEqual(JitOutcome.Deployed, r2.Outcome, r2.Message);
        rig.Remote.Events.Clear(); rig.Remote.Writes.Clear(); rig.Remote.Commands.Clear(); rig.Lines.Clear();
        return rig;
    }

    private Task<JitDeployResult> RollTo(Rig rig, string tag) =>
        rig.Deployer.RollbackToAsync(Req(), tag, l => { rig.Lines.Add(l); return Task.CompletedTask; });

    [TestMethod]
    public async Task RollingBackOnPurpose_RunsTheOldReleaseWithoutBuildingAnything()
    {
        var rig = await TwoReleasesAsync();
        var r = await RollTo(rig, "r1-aaaaaaa");

        Assert.AreEqual(JitOutcome.Deployed, r.Outcome, r.Message);
        Assert.AreEqual("r1-aaaaaaa", r.Tag);
        SeqEq(["checkout:aaaaaaa", "stop:web", "stop:api", "start:api@r1-aaaaaaa", "start:web@r1-aaaaaaa"], rig.Core);
        var s = rig.Remote.ReadState();
        Assert.AreEqual(DeployPhase.Idle, s.Phase);
        Assert.AreEqual("r1-aaaaaaa", s.CurrentTag);
        Assert.AreEqual("r2-bbbbbbb", s.PreviousTag, "you can roll forward again");
        Assert.AreEqual(2, s.Releases.Count, "no release is lost");
        Assert.IsTrue(rig.Remote.Images.Contains("shop/api:r2-bbbbbbb"));
        Assert.AreEqual("shop/web:r1-aaaaaaa", rig.Remote.Running["web"]);
    }

    [TestMethod]
    public async Task RollingBack_ToTheReleaseThatIsAlreadyCurrent_DoesNothing()
    {
        var rig = await TwoReleasesAsync();
        var r = await RollTo(rig, "r2-bbbbbbb");
        Assert.AreEqual(JitOutcome.NothingToDeploy, r.Outcome);
        Assert.AreEqual(0, rig.Core.Length);
    }

    [TestMethod]
    public async Task RollingBack_ToAReleaseThatIsUnknown_IsRefused()
    {
        var rig = await TwoReleasesAsync();
        var r = await RollTo(rig, "r9-9999999");
        Assert.AreEqual(JitOutcome.FailedBeforeChange, r.Outcome);
        StringAssert.Contains(r.Message, "r9-9999999");
        Assert.AreEqual(0, rig.Core.Length);
    }

    [TestMethod]
    public async Task RollingBack_ToAReleaseWhoseImagesAreGone_IsRefusedBeforeStoppingAnything()
    {
        var rig = await TwoReleasesAsync();
        rig.Remote.Images.Remove("shop/web:r1-aaaaaaa");
        var r = await RollTo(rig, "r1-aaaaaaa");
        Assert.AreEqual(JitOutcome.FailedBeforeChange, r.Outcome);
        Assert.IsFalse(rig.Remote.Events.Any(e => e.StartsWith("stop:")));
    }

    [TestMethod]
    public async Task ARollbackToAnOldReleaseThatFails_PutsTheCurrentReleaseBack_AndKeepsTheOldOnesImages()
    {
        var rig = await TwoReleasesAsync();
        rig.Remote.Probe = (svc, tag, _) => svc == "api" && tag == "r1-aaaaaaa" ? "running|unhealthy|0" : "running|healthy|0";
        var r = await RollTo(rig, "r1-aaaaaaa");

        Assert.AreEqual(JitOutcome.RolledBack, r.Outcome, r.Message);
        var s = rig.Remote.ReadState();
        Assert.AreEqual("r2-bbbbbbb", s.CurrentTag, "still on the release it started from");
        Assert.AreEqual(2, s.Releases.Count, "the release you tried to go back to is NOT deleted");
        Assert.IsTrue(rig.Remote.Images.Contains("shop/api:r1-aaaaaaa"));
        Assert.AreEqual("shop/api:r2-bbbbbbb", rig.Remote.Running["api"]);
    }

    [TestMethod]
    public async Task ACrashDuringAnOnPurposeRollback_IsRecoveredWithoutDeletingTheTargetRelease()
    {
        var rig = await TwoReleasesAsync();
        rig.Remote.ThrowWhen = c => c.Contains("up -d --no-deps") && c.Contains("'web'") && c.Contains("r1-aaaaaaa");
        var lost = await RollTo(rig, "r1-aaaaaaa");
        Assert.AreEqual(JitOutcome.ConnectionLost, lost.Outcome);
        rig.Remote.ThrowWhen = null;

        var r = await rig.Recover();
        Assert.AreEqual(JitOutcome.RolledBack, r.Outcome, r.Message);
        var s = rig.Remote.ReadState();
        Assert.AreEqual("r2-bbbbbbb", s.CurrentTag);
        Assert.AreEqual(2, s.Releases.Count);
        Assert.IsTrue(rig.Remote.Images.Contains("shop/api:r1-aaaaaaa"));
    }

    // ═══ concurrency ════════════════════════════════════════════════════════

    [TestMethod]
    public async Task ASecondDeployOfTheSameProject_IsRefusedWhileOneIsRunning()
    {
        var rig = new Rig();
        using var held = rig.Lock.TryAcquire(JitDeployLock.KeyFor(Target));
        Assert.IsNotNull(held);

        var r = await rig.Deploy();
        Assert.AreEqual(JitOutcome.Busy, r.Outcome);
        Assert.AreEqual(0, rig.Remote.Commands.Count);
    }

    [TestMethod]
    public async Task TheLock_IsReleasedWhenTheDeployEnds_EvenWhenItFails()
    {
        var rig = new Rig();
        rig.Remote.DockerOk = false;
        await rig.Deploy();
        using var again = rig.Lock.TryAcquire(JitDeployLock.KeyFor(Target));
        Assert.IsNotNull(again);
    }

    [TestMethod]
    public async Task DifferentProjects_DoNotBlockEachOther()
    {
        var rig = new Rig();
        using var held = rig.Lock.TryAcquire(JitDeployLock.KeyFor(new JitTarget("h", "u", "/k", "/home/deploy/other")));
        var r = await rig.Deploy();
        Assert.AreEqual(JitOutcome.Deployed, r.Outcome, r.Message);
    }

    // ═══ retention ══════════════════════════════════════════════════════════

    private static string ShaFor(int i) => new string((char)('0' + i), 40);

    [TestMethod]
    public async Task OldReleases_AreRemovedWholesale_AfterASuccessfulDeploy()
    {
        var rig = new Rig();
        for (var i = 1; i <= 5; i++)
        {
            rig.Remote.Branches["master"] = ShaFor(i);
            var r = await rig.Deploy();
            Assert.AreEqual(JitOutcome.Deployed, r.Outcome, r.Message);
        }

        var s = rig.Remote.ReadState();
        CollectionAssert.AreEqual(new[] { "r3-3333333", "r4-4444444", "r5-5555555" }, s.Releases.Select(x => x.Tag).ToArray());
        Assert.AreEqual("r5-5555555", s.CurrentTag);
        Assert.AreEqual("r4-4444444", s.PreviousTag);
        CollectionAssert.AreEquivalent(
            new[] { "shop/api:r3-3333333", "shop/web:r3-3333333", "shop/api:r4-4444444", "shop/web:r4-4444444", "shop/api:r5-5555555", "shop/web:r5-5555555" },
            rig.Remote.Images.ToArray());
    }

    [TestMethod]
    public async Task TheKeepCount_IsTunable_ButNeverBelowTwo()
    {
        var rig = new Rig();
        for (var i = 1; i <= 4; i++)
        {
            rig.Remote.Branches["master"] = ShaFor(i);
            await rig.Deploy(Req() with { Settings = new JitDeploySettings { ReleasesToKeep = 1 } });
        }
        Assert.AreEqual(2, rig.Remote.ReadState().Releases.Count, "a keep count of 1 is raised to the minimum of 2");

        for (var i = 5; i <= 8; i++)
        {
            rig.Remote.Branches["master"] = ShaFor(i);
            await rig.Deploy(Req() with { Settings = new JitDeploySettings { ReleasesToKeep = 5 } });
        }
        Assert.AreEqual(5, rig.Remote.ReadState().Releases.Count);
    }

    [TestMethod]
    public async Task TheStopGracePeriod_IsPassedToEveryStop()
    {
        var rig = new Rig();
        await rig.Deploy(Req() with { Settings = new JitDeploySettings { StopGraceSeconds = 45 } });
        Assert.IsTrue(rig.Remote.Commands.Where(c => c.Contains(" stop -t ")).All(c => c.Contains("stop -t 45 ")));
    }
}
