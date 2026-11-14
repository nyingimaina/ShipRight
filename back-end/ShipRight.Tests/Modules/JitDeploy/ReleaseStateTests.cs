using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.JitDeploy;
using ShipRight.Shared.SshRunner;

namespace ShipRight.Tests.Modules.JitDeploy;

[TestClass]
public class ReleaseStateTests
{
    private static readonly DateTime T0 = new(2026, 11, 13, 10, 0, 0, DateTimeKind.Utc);

    // ── transitions ──────────────────────────────────────────────────────────

    [DataTestMethod]
    [DataRow(DeployPhase.Idle, DeployPhase.Syncing)]
    [DataRow(DeployPhase.Syncing, DeployPhase.Building)]
    [DataRow(DeployPhase.Syncing, DeployPhase.Idle)]            // sync failed: nothing was touched
    [DataRow(DeployPhase.Building, DeployPhase.OldStopped)]
    [DataRow(DeployPhase.Building, DeployPhase.FailedBuild)]
    [DataRow(DeployPhase.FailedBuild, DeployPhase.Idle)]
    [DataRow(DeployPhase.OldStopped, DeployPhase.NewStarted)]
    [DataRow(DeployPhase.OldStopped, DeployPhase.RollingBack)]
    [DataRow(DeployPhase.NewStarted, DeployPhase.Verified)]
    [DataRow(DeployPhase.NewStarted, DeployPhase.RollingBack)]
    [DataRow(DeployPhase.Verified, DeployPhase.Idle)]
    [DataRow(DeployPhase.RollingBack, DeployPhase.RolledBack)]
    [DataRow(DeployPhase.RollingBack, DeployPhase.RollbackFailed)]
    [DataRow(DeployPhase.RolledBack, DeployPhase.Idle)]
    [DataRow(DeployPhase.RollbackFailed, DeployPhase.RollingBack)]   // retry
    [DataRow(DeployPhase.RollbackFailed, DeployPhase.Idle)]          // operator fixed it by hand
    public void AllowedTransitions(DeployPhase from, DeployPhase to)
    {
        Assert.IsTrue(ReleaseStateMachine.CanMove(from, to), $"{from} -> {to}");
    }

    [DataTestMethod]
    [DataRow(DeployPhase.Idle, DeployPhase.OldStopped)]
    [DataRow(DeployPhase.Idle, DeployPhase.NewStarted)]
    [DataRow(DeployPhase.Idle, DeployPhase.Idle)]
    [DataRow(DeployPhase.Building, DeployPhase.NewStarted)]      // cannot start new before stopping old
    [DataRow(DeployPhase.Building, DeployPhase.Idle)]            // a failed build is recorded, not hidden
    [DataRow(DeployPhase.OldStopped, DeployPhase.Idle)]          // would abandon stopped containers
    [DataRow(DeployPhase.NewStarted, DeployPhase.Idle)]
    [DataRow(DeployPhase.Verified, DeployPhase.RollingBack)]     // verified is final
    [DataRow(DeployPhase.RolledBack, DeployPhase.Syncing)]
    [DataRow(DeployPhase.RollbackFailed, DeployPhase.Syncing)]   // no new deploy on a broken host
    [DataRow(DeployPhase.FailedBuild, DeployPhase.Syncing)]
    public void ForbiddenTransitions(DeployPhase from, DeployPhase to)
    {
        Assert.IsFalse(ReleaseStateMachine.CanMove(from, to), $"{from} -> {to}");
    }

    [TestMethod]
    public void MoveTo_Illegal_ThrowsAndNamesBothPhases()
    {
        var ex = Assert.ThrowsException<InvalidTransitionException>(() => ReleaseState.Empty.MoveTo(DeployPhase.NewStarted, T0));
        StringAssert.Contains(ex.Message, "Idle");
        StringAssert.Contains(ex.Message, "NewStarted");
    }

    [TestMethod]
    public void MoveTo_Legal_ReturnsANewStateWithThePhaseAndTime()
    {
        var s = ReleaseState.Empty.MoveTo(DeployPhase.Syncing, T0);
        Assert.AreEqual(DeployPhase.Syncing, s.Phase);
        Assert.AreEqual(T0, s.UpdatedUtc);
        Assert.AreEqual(DeployPhase.Idle, ReleaseState.Empty.Phase, "the original is not changed");
    }

    // ── recovery after a crash ───────────────────────────────────────────────

    [DataTestMethod]
    [DataRow(DeployPhase.Idle, RecoveryAction.None)]
    [DataRow(DeployPhase.Syncing, RecoveryAction.Reset)]
    [DataRow(DeployPhase.Building, RecoveryAction.Reset)]
    [DataRow(DeployPhase.FailedBuild, RecoveryAction.Reset)]
    [DataRow(DeployPhase.RolledBack, RecoveryAction.Reset)]
    [DataRow(DeployPhase.OldStopped, RecoveryAction.Rollback)]
    [DataRow(DeployPhase.NewStarted, RecoveryAction.Rollback)]
    [DataRow(DeployPhase.RollingBack, RecoveryAction.Rollback)]
    [DataRow(DeployPhase.Verified, RecoveryAction.Commit)]
    [DataRow(DeployPhase.RollbackFailed, RecoveryAction.Manual)]
    public void WhatToDoWhenFoundAfterACrash(DeployPhase phase, RecoveryAction expected)
    {
        Assert.AreEqual(expected, ReleaseStateMachine.Recovery(phase));
    }

    [TestMethod]
    public void EveryPhaseHasARecoveryAction()
    {
        foreach (var p in Enum.GetValues<DeployPhase>()) ReleaseStateMachine.Recovery(p);
    }

    // ── json ─────────────────────────────────────────────────────────────────

    private static ReleaseState Sample() => new()
    {
        Phase = DeployPhase.Idle,
        CurrentTag = "r2-bbbbbbb",
        PreviousTag = "r1-aaaaaaa",
        UpdatedUtc = T0,
        Releases =
        [
            new ReleaseRecord("r1-aaaaaaa", "aaaaaaa1111111111111111111111111111111111", ["shop/api:r1-aaaaaaa", "shop/web:r1-aaaaaaa"], T0.AddDays(-1), "first"),
            new ReleaseRecord("r2-bbbbbbb", "bbbbbbb2222222222222222222222222222222222", ["shop/api:r2-bbbbbbb", "shop/web:r2-bbbbbbb"], T0),
        ],
    };

    [TestMethod]
    public void Json_RoundTrips()
    {
        var back = ReleaseStateJson.Deserialize(ReleaseStateJson.Serialize(Sample()));
        Assert.AreEqual(DeployPhase.Idle, back.Phase);
        Assert.AreEqual("r2-bbbbbbb", back.CurrentTag);
        Assert.AreEqual("r1-aaaaaaa", back.PreviousTag);
        Assert.AreEqual(2, back.Releases.Count);
        CollectionAssert.AreEqual(new[] { "shop/api:r1-aaaaaaa", "shop/web:r1-aaaaaaa" }, back.Releases[0].Images.ToArray());
        Assert.AreEqual("first", back.Releases[0].Message);
        Assert.AreEqual(T0, back.UpdatedUtc);
    }

    [TestMethod]
    public void Json_IsPlainAndHumanReadable_WithNothingThatNamesTheDeployer()
    {
        var json = ReleaseStateJson.Serialize(Sample());
        StringAssert.Contains(json, "\"phase\": \"idle\"");
        StringAssert.Contains(json, "\"currentTag\": \"r2-bbbbbbb\"");
        StringAssert.Contains(json, "\n");   // indented
        Assert.IsFalse(json.Contains("ShipRight", StringComparison.OrdinalIgnoreCase));
    }

    [DataTestMethod]
    [DataRow("")]
    [DataRow("not json")]
    [DataRow("[]")]
    [DataRow("{\"phase\": \"flying\"}")]
    public void Deserialize_OfCorruptState_ThrowsAClearError_NeverAnEmptyState(string json)
    {
        var ex = Assert.ThrowsException<StateCorruptException>(() => ReleaseStateJson.Deserialize(json));
        StringAssert.Contains(ex.Message, "state");
    }

    [TestMethod]
    public void Deserialize_OfANewerFormat_Throws_SoOldCodeNeverOverwritesIt()
    {
        var ex = Assert.ThrowsException<StateCorruptException>(() => ReleaseStateJson.Deserialize("{\"version\": 99, \"phase\": \"idle\"}"));
        StringAssert.Contains(ex.Message, "newer");
    }

    [TestMethod]
    public void Deserialize_ToleratesUnknownExtraFields()
    {
        var s = ReleaseStateJson.Deserialize("{\"version\": 1, \"phase\": \"idle\", \"someFutureField\": 5}");
        Assert.AreEqual(DeployPhase.Idle, s.Phase);
    }

    // ── store ────────────────────────────────────────────────────────────────

    private sealed class FakeSsh : ISshRunner
    {
        public List<string> Commands { get; } = [];
        public string[] StdoutLines { get; set; } = [];
        public string[] StderrLines { get; set; } = [];
        public int Exit { get; set; }
        public Exception? Throw { get; set; }

        public async Task<int> RunAsync(string host, string username, string keyPath, string command,
            Func<string, Task>? onOutput = null, Func<string, Task>? onStderr = null, CancellationToken ct = default)
        {
            Commands.Add(command);
            if (Throw is not null) throw Throw;
            foreach (var l in StdoutLines) if (onOutput is not null) await onOutput(l);
            foreach (var l in StderrLines) { if (onStderr is not null) await onStderr(l); else if (onOutput is not null) await onOutput(l); }
            return Exit;
        }
    }

    private static readonly JitTarget Target = new("10.0.0.5", "deploy", "/keys/k", "/home/deploy/shop");

    [TestMethod]
    public void ReadCommand_PrintsTheFile_OrASentinelWhenThereIsNone()
    {
        var cmd = ReleaseStateStore.BuildReadCommand(Target);
        StringAssert.Contains(cmd, "'/home/deploy/shop/.deploy-state/state.json'");
        StringAssert.Contains(cmd, "cat ");
        StringAssert.Contains(cmd, ReleaseStateStore.NoStateSentinel);
    }

    [TestMethod]
    public async Task Read_WhenThereIsNoFile_ReturnsAnEmptyIdleState()
    {
        var ssh = new FakeSsh { StdoutLines = [ReleaseStateStore.NoStateSentinel] };
        var s = await new ReleaseStateStore(ssh).ReadAsync(Target);
        Assert.AreEqual(DeployPhase.Idle, s.Phase);
        Assert.AreEqual(0, s.Releases.Count);
        Assert.IsNull(s.CurrentTag);
    }

    [TestMethod]
    public async Task Read_ParsesTheFileFromItsLines()
    {
        var ssh = new FakeSsh { StdoutLines = ReleaseStateJson.Serialize(Sample()).Split('\n') };
        var s = await new ReleaseStateStore(ssh).ReadAsync(Target);
        Assert.AreEqual("r2-bbbbbbb", s.CurrentTag);
        Assert.AreEqual(2, s.Releases.Count);
    }

    [TestMethod]
    public async Task Read_IgnoresStderrNoise()
    {
        var ssh = new FakeSsh { StdoutLines = [ReleaseStateStore.NoStateSentinel], StderrLines = ["Warning: Permanently added host"] };
        var s = await new ReleaseStateStore(ssh).ReadAsync(Target);
        Assert.AreEqual(DeployPhase.Idle, s.Phase);
    }

    [TestMethod]
    public async Task Read_OfACorruptFile_Throws_RatherThanPretendingThereIsNoState()
    {
        var ssh = new FakeSsh { StdoutLines = ["{ this is not json"] };
        await Assert.ThrowsExceptionAsync<StateCorruptException>(() => new ReleaseStateStore(ssh).ReadAsync(Target));
    }

    [TestMethod]
    public async Task Read_OfAnEmptyFile_Throws_AsCorrupt()
    {
        var ssh = new FakeSsh { StdoutLines = [] };
        await Assert.ThrowsExceptionAsync<StateCorruptException>(() => new ReleaseStateStore(ssh).ReadAsync(Target));
    }

    [TestMethod]
    public async Task Read_WhenTheCommandFails_ThrowsWithTheExitCode()
    {
        var ssh = new FakeSsh { Exit = 255 };
        var ex = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => new ReleaseStateStore(ssh).ReadAsync(Target));
        StringAssert.Contains(ex.Message, "255");
    }

    [TestMethod]
    public async Task Read_UsesTheTargetsConnectionDetails()
    {
        var seen = new List<(string, string, string)>();
        var ssh = new RecordingTargets(seen);
        await new ReleaseStateStore(ssh).ReadAsync(Target);
        Assert.AreEqual(("10.0.0.5", "deploy", "/keys/k"), seen.Single());
    }

    private sealed class RecordingTargets(List<(string, string, string)> seen) : ISshRunner
    {
        public async Task<int> RunAsync(string host, string username, string keyPath, string command,
            Func<string, Task>? onOutput = null, Func<string, Task>? onStderr = null, CancellationToken ct = default)
        {
            seen.Add((host, username, keyPath));
            if (onOutput is not null) await onOutput(ReleaseStateStore.NoStateSentinel);
            return 0;
        }
    }

    [TestMethod]
    public void WriteCommand_WritesBase64ToATempFileThenRenames_SoAHalfWrittenFileIsNeverSeen()
    {
        var cmd = ReleaseStateStore.BuildWriteCommand(Target, Sample());
        var iTmp = cmd.IndexOf("state.json.tmp", StringComparison.Ordinal);
        var iMv = cmd.IndexOf("mv -f", StringComparison.Ordinal);
        Assert.IsTrue(iTmp > 0 && iMv > iTmp, cmd);
        StringAssert.Contains(cmd, "mkdir -p '/home/deploy/shop/.deploy-state'");
        StringAssert.Contains(cmd, "base64 -d");
        Assert.IsFalse(cmd.Contains("\"phase\""), "the JSON itself must not appear in the command");
    }

    [TestMethod]
    public void WriteCommand_CarriesTheSerialisedState()
    {
        var cmd = ReleaseStateStore.BuildWriteCommand(Target, Sample());
        var start = cmd.IndexOf("echo '", StringComparison.Ordinal) + 6;
        var b64 = cmd[start..cmd.IndexOf('\'', start)];
        Assert.AreEqual(ReleaseStateJson.Serialize(Sample()), Encoding.UTF8.GetString(Convert.FromBase64String(b64)).TrimEnd('\n'));
    }

    [TestMethod]
    public async Task Write_SendsOneCommand_AndThrowsOnFailure()
    {
        var ssh = new FakeSsh();
        await new ReleaseStateStore(ssh).WriteAsync(Target, Sample());
        Assert.AreEqual(1, ssh.Commands.Count);

        var failing = new FakeSsh { Exit = 1 };
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => new ReleaseStateStore(failing).WriteAsync(Target, Sample()));
    }
}
