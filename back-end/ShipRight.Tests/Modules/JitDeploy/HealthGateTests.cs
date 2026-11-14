using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.JitDeploy;

namespace ShipRight.Tests.Modules.JitDeploy;

[TestClass]
public class HealthGateTests
{
    private static readonly GateOptions Opts = new(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(10));

    private static GateDecision D(ContainerState s, HealthStatus h, int seconds, int restarts = 0, int atStart = 0) =>
        HealthGate.Decide(new GateObservation(s, h, restarts, atStart, TimeSpan.FromSeconds(seconds)), Opts);

    [TestMethod]
    public void Defaults_AreSixtySecondsAndTenSeconds()
    {
        var d = new GateOptions();
        Assert.AreEqual(TimeSpan.FromSeconds(60), d.HealthTimeout);
        Assert.AreEqual(TimeSpan.FromSeconds(10), d.SettleTime);
    }

    [TestMethod]
    public void Healthy_Passes()
    {
        Assert.AreEqual(GateVerdict.Pass, D(ContainerState.Running, HealthStatus.Healthy, 3).Verdict);
    }

    [TestMethod]
    public void Starting_WaitsUntilTheTimeout_ThenFails()
    {
        Assert.AreEqual(GateVerdict.Wait, D(ContainerState.Running, HealthStatus.Starting, 59).Verdict);
        var d = D(ContainerState.Running, HealthStatus.Starting, 60);
        Assert.AreEqual(GateVerdict.Fail, d.Verdict);
        StringAssert.Contains(d.Reason, "60");
    }

    [TestMethod]
    public void Unhealthy_FailsImmediately()
    {
        var d = D(ContainerState.Running, HealthStatus.Unhealthy, 1);
        Assert.AreEqual(GateVerdict.Fail, d.Verdict);
        StringAssert.Contains(d.Reason, "unhealthy");
    }

    [DataTestMethod]
    [DataRow(ContainerState.Exited)]
    [DataRow(ContainerState.Dead)]
    public void AnExitedContainer_FailsImmediately_WhateverItsHealth(ContainerState state)
    {
        var d = D(state, HealthStatus.Healthy, 1);
        Assert.AreEqual(GateVerdict.Fail, d.Verdict);
        StringAssert.Contains(d.Reason, "exited");
    }

    [TestMethod]
    public void NoHealthcheck_PassesOnceItHasStayedUpForTheSettleTime()
    {
        Assert.AreEqual(GateVerdict.Wait, D(ContainerState.Running, HealthStatus.None, 9).Verdict);
        Assert.AreEqual(GateVerdict.Pass, D(ContainerState.Running, HealthStatus.None, 10).Verdict);
    }

    [TestMethod]
    public void ARestartingContainer_Fails_AsACrashLoop()
    {
        var d = D(ContainerState.Restarting, HealthStatus.None, 2);
        Assert.AreEqual(GateVerdict.Fail, d.Verdict);
        StringAssert.Contains(d.Reason, "restart");
    }

    [TestMethod]
    public void ARisingRestartCount_Fails_EvenIfItLooksRunningRightNow()
    {
        var d = D(ContainerState.Running, HealthStatus.Healthy, 5, restarts: 2, atStart: 1);
        Assert.AreEqual(GateVerdict.Fail, d.Verdict);
        StringAssert.Contains(d.Reason, "restart");
    }

    [TestMethod]
    public void ARestartCountThatDidNotRise_IsFine()
    {
        Assert.AreEqual(GateVerdict.Pass, D(ContainerState.Running, HealthStatus.Healthy, 5, restarts: 3, atStart: 3).Verdict);
    }

    [DataTestMethod]
    [DataRow(ContainerState.Created)]
    [DataRow(ContainerState.Paused)]
    public void ANotYetRunningContainer_WaitsThenFailsAtTheTimeout(ContainerState state)
    {
        Assert.AreEqual(GateVerdict.Wait, D(state, HealthStatus.None, 30).Verdict);
        Assert.AreEqual(GateVerdict.Fail, D(state, HealthStatus.None, 60).Verdict);
    }

    [DataTestMethod]
    [DataRow("running", ContainerState.Running)]
    [DataRow("exited", ContainerState.Exited)]
    [DataRow("restarting", ContainerState.Restarting)]
    [DataRow("created", ContainerState.Created)]
    [DataRow("paused", ContainerState.Paused)]
    [DataRow("dead", ContainerState.Dead)]
    [DataRow("removing", ContainerState.Dead)]
    [DataRow(" Running \n", ContainerState.Running)]
    public void ParseState_ReadsDockerInspectStatus(string raw, ContainerState expected)
    {
        Assert.AreEqual(expected, HealthGate.ParseState(raw));
    }

    [DataTestMethod]
    [DataRow("healthy", HealthStatus.Healthy)]
    [DataRow("unhealthy", HealthStatus.Unhealthy)]
    [DataRow("starting", HealthStatus.Starting)]
    [DataRow("", HealthStatus.None)]
    [DataRow("none", HealthStatus.None)]
    [DataRow("<no value>", HealthStatus.None)]
    [DataRow(" Healthy\n", HealthStatus.Healthy)]
    public void ParseHealth_ReadsDockerInspectHealth_AndTreatsMissingAsNone(string raw, HealthStatus expected)
    {
        Assert.AreEqual(expected, HealthGate.ParseHealth(raw));
    }

    [TestMethod]
    public void ParseState_OfSomethingUnknown_Throws_SoItCannotSilentlyPass()
    {
        Assert.ThrowsException<ArgumentException>(() => HealthGate.ParseState("flying"));
    }
}
