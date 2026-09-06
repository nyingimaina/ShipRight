using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Resources.Models;
using ShipRight.Modules.Projects;

namespace ShipRight.Tests.Modules.Resources;

[TestClass]
public class PipelineExecutorTests
{
    private static PipelineResource MakePipeline(List<PipelineStep>? steps = null) => new()
    {
        Name = "test pipeline",
        Steps = steps ?? [],
        Scope = PipelineScope.Global,
        CreatedAt = DateTime.UtcNow,
        ModifiedAt = DateTime.UtcNow,
    };

    private static PipelineStep ScriptStep(Guid? scriptId = null, string? label = null, bool continueOnError = false) => new()
    {
        Id = Guid.NewGuid(),
        Type = PipelineStepType.Script,
        ScriptResourceId = scriptId ?? Guid.NewGuid(),
        Label = label ?? "script step",
        ContinueOnError = continueOnError,
    };

    private static PipelineStep BuildStep() => new()
    {
        Id = Guid.NewGuid(),
        Type = PipelineStepType.Build,
    };

    private static PipelineStep PushStep() => new()
    {
        Id = Guid.NewGuid(),
        Type = PipelineStepType.Push,
    };

    private static PipelineStep DeployStep(DeployMode mode = DeployMode.GitScript) => new()
    {
        Id = Guid.NewGuid(),
        Type = PipelineStepType.Deploy,
        DeployMode = mode,
    };

    [TestMethod]
    public void ValidateSteps_EmptyPipeline_IsValid()
    {
        var pipeline = MakePipeline([]);
        var errors = ShipRight.Modules.Resources.PipelineExecutor.ValidateSteps(pipeline.Steps);
        Assert.AreEqual(0, errors.Count);
    }

    [TestMethod]
    public void ValidateSteps_ScriptOnly_IsValid()
    {
        var steps = new List<PipelineStep> { ScriptStep() };
        var errors = ShipRight.Modules.Resources.PipelineExecutor.ValidateSteps(steps);
        Assert.AreEqual(0, errors.Count);
    }

    [TestMethod]
    public void ValidateSteps_BuildOnly_IsValid()
    {
        var steps = new List<PipelineStep> { BuildStep() };
        var errors = ShipRight.Modules.Resources.PipelineExecutor.ValidateSteps(steps);
        Assert.AreEqual(0, errors.Count);
    }

    [TestMethod]
    public void ValidateSteps_PushBeforeBuild_ReturnsError()
    {
        var steps = new List<PipelineStep> { PushStep(), BuildStep() };
        var errors = ShipRight.Modules.Resources.PipelineExecutor.ValidateSteps(steps);
        Assert.IsTrue(errors.Count > 0);
        Assert.IsTrue(errors.Any(e => e.Contains("Push") && e.Contains("Build")));
    }

    [TestMethod]
    public void ValidateSteps_DeployBeforePush_ReturnsError()
    {
        var steps = new List<PipelineStep> { DeployStep(), PushStep() };
        var errors = ShipRight.Modules.Resources.PipelineExecutor.ValidateSteps(steps);
        Assert.IsTrue(errors.Count > 0);
        Assert.IsTrue(errors.Any(e => e.Contains("Deploy") && e.Contains("Push")));
    }

    [TestMethod]
    public void ValidateSteps_DeployBeforeBuild_ReturnsError()
    {
        var steps = new List<PipelineStep> { DeployStep(), BuildStep() };
        var errors = ShipRight.Modules.Resources.PipelineExecutor.ValidateSteps(steps);
        Assert.IsTrue(errors.Count > 0);
    }

    [TestMethod]
    public void ValidateSteps_CorrectOrder_IsValid()
    {
        var steps = new List<PipelineStep> { BuildStep(), PushStep(), DeployStep() };
        var errors = ShipRight.Modules.Resources.PipelineExecutor.ValidateSteps(steps);
        Assert.AreEqual(0, errors.Count);
    }

    [TestMethod]
    public void ValidateSteps_ScriptsBetweenFixedSteps_IsValid()
    {
        var steps = new List<PipelineStep>
        {
            ScriptStep(label: "pre-build"),
            BuildStep(),
            ScriptStep(label: "post-build"),
            PushStep(),
            ScriptStep(label: "post-push"),
            DeployStep(),
            ScriptStep(label: "post-deploy"),
        };
        var errors = ShipRight.Modules.Resources.PipelineExecutor.ValidateSteps(steps);
        Assert.AreEqual(0, errors.Count);
    }

    [TestMethod]
    public void ValidateSteps_AllScriptsOnly_IsValid()
    {
        var steps = new List<PipelineStep>
        {
            ScriptStep(label: "one"),
            ScriptStep(label: "two"),
            ScriptStep(label: "three"),
        };
        var errors = ShipRight.Modules.Resources.PipelineExecutor.ValidateSteps(steps);
        Assert.AreEqual(0, errors.Count);
    }

    [TestMethod]
    public void ValidateSteps_MissingScriptResourceId_ReturnsError()
    {
        var steps = new List<PipelineStep>
        {
            new() { Id = Guid.NewGuid(), Type = PipelineStepType.Script, Label = "no script" }
        };
        var errors = ShipRight.Modules.Resources.PipelineExecutor.ValidateSteps(steps);
        Assert.IsTrue(errors.Count > 0);
        Assert.IsTrue(errors.Any(e => e.Contains("ScriptResourceId")));
    }

    [TestMethod]
    public void ValidateSteps_DuplicateBuild_ReturnsError()
    {
        var steps = new List<PipelineStep> { BuildStep(), ScriptStep(), BuildStep() };
        var errors = ShipRight.Modules.Resources.PipelineExecutor.ValidateSteps(steps);
        Assert.IsTrue(errors.Count > 0);
        Assert.IsTrue(errors.Any(e => e.Contains("Build") && e.Contains("multiple")));
    }

    [TestMethod]
    public void ValidateSteps_DuplicatePush_ReturnsError()
    {
        var steps = new List<PipelineStep> { PushStep(), ScriptStep(), PushStep() };
        var errors = ShipRight.Modules.Resources.PipelineExecutor.ValidateSteps(steps);
        Assert.IsTrue(errors.Count > 0);
        Assert.IsTrue(errors.Any(e => e.Contains("Push") && e.Contains("multiple")));
    }

    [TestMethod]
    public void ValidateSteps_DuplicateDeploy_ReturnsError()
    {
        var steps = new List<PipelineStep> { DeployStep(), ScriptStep(), DeployStep() };
        var errors = ShipRight.Modules.Resources.PipelineExecutor.ValidateSteps(steps);
        Assert.IsTrue(errors.Count > 0);
        Assert.IsTrue(errors.Any(e => e.Contains("Deploy") && e.Contains("multiple")));
    }

    [TestMethod]
    public void GroupSteps_ByPosition_CorrectGroups()
    {
        var steps = new List<PipelineStep>
        {
            ScriptStep(label: "pre-build"),
            ScriptStep(label: "pre-build-2"),
            BuildStep(),
            ScriptStep(label: "post-build"),
            PushStep(),
            ScriptStep(label: "post-push"),
            DeployStep(),
            ScriptStep(label: "post-deploy"),
        };

        var groups = ShipRight.Modules.Resources.PipelineExecutor.GroupStepsByPosition(steps);

        Assert.AreEqual(2, groups.PreBuild.Count);
        Assert.AreEqual(1, groups.PrePush.Count);
        Assert.AreEqual(1, groups.PreDeploy.Count);
        Assert.AreEqual(1, groups.PostDeploy.Count);
    }

    [TestMethod]
    public void GroupSteps_NoBuildStep_ScriptsGoToPrePush()
    {
        var steps = new List<PipelineStep>
        {
            ScriptStep(label: "before-push"),
            PushStep(),
            DeployStep(),
        };

        var groups = ShipRight.Modules.Resources.PipelineExecutor.GroupStepsByPosition(steps);

        Assert.AreEqual(1, groups.PrePush.Count);
        Assert.AreEqual("before-push", groups.PrePush[0].Label);
    }

    [TestMethod]
    public void GroupSteps_NoPushStep_ScriptsGoToPreDeploy()
    {
        var steps = new List<PipelineStep>
        {
            BuildStep(),
            ScriptStep(label: "before-deploy"),
            DeployStep(),
        };

        var groups = ShipRight.Modules.Resources.PipelineExecutor.GroupStepsByPosition(steps);

        Assert.AreEqual(1, groups.PreDeploy.Count);
        Assert.AreEqual("before-deploy", groups.PreDeploy[0].Label);
    }

    [TestMethod]
    public void GroupSteps_OnlyScripts_AllGoToPreBuild()
    {
        var steps = new List<PipelineStep>
        {
            ScriptStep(label: "one"),
            ScriptStep(label: "two"),
        };

        var groups = ShipRight.Modules.Resources.PipelineExecutor.GroupStepsByPosition(steps);

        Assert.AreEqual(2, groups.PreBuild.Count);
        Assert.AreEqual(0, groups.PrePush.Count);
    }

    [TestMethod]
    public void GroupSteps_EmptyPipeline_AllGroupsEmpty()
    {
        var steps = new List<PipelineStep>();
        var groups = ShipRight.Modules.Resources.PipelineExecutor.GroupStepsByPosition(steps);

        Assert.AreEqual(0, groups.PreBuild.Count);
        Assert.AreEqual(0, groups.PrePush.Count);
        Assert.AreEqual(0, groups.PreDeploy.Count);
        Assert.AreEqual(0, groups.PostDeploy.Count);
    }
}
