using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Platform;

namespace ShipRight.Tests.Modules.Platform;

[TestClass]
public class RuleEngineTests
{
    private static RuleDefinition Rule(
        string id = "server.ssh",
        RuleScope scope = RuleScope.Project,
        RuleSeverity severity = RuleSeverity.Blocking,
        string checkId = "server.ssh") =>
        new(id, "SSH access", "The server can be reached.", scope, severity, checkId);

    private static RuleWaiver Waiver(
        string ruleId = "server.ssh",
        string projectId = "p1",
        string? environment = null,
        DateTimeOffset? expires = null) =>
        new(ruleId, projectId, environment, "Being fixed this week.", "amara", expires ?? DateTimeOffset.UtcNow.AddDays(7));

    private static RuleContext Context(
        string? projectId = "p1", string? environment = null, bool ssh = false) =>
        new(ProjectId: projectId, Environment: environment, HasSshAccess: ssh);

    [TestMethod]
    public void PassingRule_DoesNotBlock()
    {
        var evaluation = new RuleEngine().Evaluate(Context(ssh: true), [Rule()]);

        Assert.IsFalse(evaluation.Blocked);
        Assert.AreEqual(RuleStatus.Pass, evaluation.Results.Single().Status);
    }

    [TestMethod]
    public void BlockingRule_Blocks()
    {
        var evaluation = new RuleEngine().Evaluate(Context(), [Rule()]);

        Assert.IsTrue(evaluation.Blocked);
        Assert.AreEqual(RuleStatus.Fail, evaluation.Results.Single().Status);
        Assert.AreEqual(1, evaluation.Failures.Count);
    }

    [TestMethod]
    public void AdvisoryRule_DoesNotBlock()
    {
        var evaluation = new RuleEngine().Evaluate(Context(), [Rule(severity: RuleSeverity.Advisory)]);

        Assert.IsFalse(evaluation.Blocked);
        Assert.AreEqual(RuleStatus.Fail, evaluation.Results.Single().Status);
    }

    [TestMethod]
    public void LockedRule_CannotBeWaived()
    {
        var evaluation = new RuleEngine().Evaluate(
            Context(), [Rule(severity: RuleSeverity.Locked)], [Waiver()]);

        Assert.IsTrue(evaluation.Blocked);
        Assert.AreEqual(RuleStatus.Fail, evaluation.Results.Single().Status);
    }

    [TestMethod]
    public void Waiver_TurnsAFailureIntoWaived()
    {
        var evaluation = new RuleEngine().Evaluate(Context(), [Rule()], [Waiver()]);

        Assert.IsFalse(evaluation.Blocked);
        Assert.AreEqual(RuleStatus.Waived, evaluation.Results.Single().Status);
    }

    [TestMethod]
    public void ExpiredWaiver_IsIgnored()
    {
        var expired = Waiver(expires: DateTimeOffset.UtcNow.AddDays(-1));

        var evaluation = new RuleEngine().Evaluate(Context(), [Rule()], [expired]);

        Assert.IsTrue(evaluation.Blocked);
        Assert.AreEqual(RuleStatus.Fail, evaluation.Results.Single().Status);
    }

    [TestMethod]
    public void WaiverForAnotherProject_DoesNotApply()
    {
        var evaluation = new RuleEngine().Evaluate(
            Context(projectId: "p2"), [Rule()], [Waiver(projectId: "p1")]);

        Assert.IsTrue(evaluation.Blocked);
    }

    [TestMethod]
    public void MonotonicTightening_ABroaderLockedRuleCannotBeWeakenedByANarrowerOne()
    {
        var broader = Rule(scope: RuleScope.Project, severity: RuleSeverity.Locked);
        var narrower = Rule(scope: RuleScope.Environment, severity: RuleSeverity.Advisory);

        var evaluation = new RuleEngine().Evaluate(
            Context(environment: "production"), [narrower, broader], [Waiver()]);

        var result = evaluation.Results.Single();
        Assert.AreEqual(RuleSeverity.Locked, result.Severity);
        Assert.AreEqual(RuleStatus.Fail, result.Status);
        Assert.IsTrue(evaluation.Blocked);
    }

    [TestMethod]
    public void UnknownLockedCheck_FailsClosed()
    {
        var evaluation = new RuleEngine().Evaluate(
            Context(), [Rule(id: "future.check", severity: RuleSeverity.Locked, checkId: "not.implemented")]);

        Assert.AreEqual(RuleStatus.Unknown, evaluation.Results.Single().Status);
        Assert.IsTrue(evaluation.Blocked);
    }

    [TestMethod]
    public void UnknownAdvisoryCheck_DoesNotBlock()
    {
        var evaluation = new RuleEngine().Evaluate(
            Context(), [Rule(id: "future.check", severity: RuleSeverity.Advisory, checkId: "not.implemented")]);

        Assert.AreEqual(RuleStatus.Unknown, evaluation.Results.Single().Status);
        Assert.IsFalse(evaluation.Blocked);
    }

    [TestMethod]
    public void LegacyProblems_BecomeAdvisoryResults()
    {
        var evaluation = LegacyRuleAdapter.FromProblems(["Pick the compose file.", "Add the server."]);

        Assert.AreEqual(2, evaluation.Results.Count);
        Assert.IsTrue(evaluation.Results.All(r => r.Severity == RuleSeverity.Advisory));
        Assert.IsTrue(evaluation.Results.All(r => r.Status == RuleStatus.Fail));
        Assert.IsFalse(evaluation.Blocked);
    }

    [TestMethod]
    public void ProductionWithoutDomain_FailsTheTlsRule()
    {
        var rule = Rule(id: "tls.domain", checkId: "tls.domain");
        var context = new RuleContext(Production: true, HasDomain: false);

        var evaluation = new RuleEngine().Evaluate(context, [rule]);

        Assert.IsTrue(evaluation.Blocked);
    }
}
