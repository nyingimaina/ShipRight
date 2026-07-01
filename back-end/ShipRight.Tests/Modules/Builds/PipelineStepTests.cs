using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Builds;

namespace ShipRight.Tests.Modules.Builds;

[TestClass]
public class PipelineStepTests
{
    [TestMethod]
    public void AllSteps_AreConsecutiveStartingAt1()
    {
        var numbers = PipelineStep.All.Select(s => s.Number).OrderBy(n => n).ToList();
        for (int i = 0; i < numbers.Count; i++)
            Assert.AreEqual(i + 1, numbers[i],
                $"Step numbers must be consecutive starting at 1; gap or duplicate at index {i}");
    }

    [TestMethod]
    public void NumberOf_PreconditionCheck_Returns1()
    {
        Assert.AreEqual(1, PipelineStep.NumberOf("PreconditionCheck"));
    }

    [TestMethod]
    public void NumberOf_GitStatusCheck_Returns2()
    {
        Assert.AreEqual(2, PipelineStep.NumberOf("GitStatusCheck"));
    }

    [TestMethod]
    public void NumberOf_BranchCheck_Returns3()
    {
        Assert.AreEqual(3, PipelineStep.NumberOf("BranchCheck"));
    }

    [TestMethod]
    public void NumberOf_BuildComplete_Returns7()
    {
        Assert.AreEqual(7, PipelineStep.NumberOf("BuildComplete"));
    }

    [TestMethod]
    public void NumberOf_UnknownStep_Throws()
    {
        Assert.ThrowsException<InvalidOperationException>(
            () => PipelineStep.NumberOf("NonExistentStep"),
            "Unknown step names must throw, not silently return 0");
    }

    [TestMethod]
    public void AllSteps_HaveUniqueNames()
    {
        var names = PipelineStep.All.Select(s => s.Name).ToList();
        var distinctNames = names.Distinct().ToList();
        Assert.AreEqual(names.Count, distinctNames.Count, "All step names must be unique");
    }

    [TestMethod]
    public void AllSteps_ContainsExpectedStepNames()
    {
        var names = PipelineStep.All.Select(s => s.Name).ToHashSet();
        foreach (var expected in new[]
        {
            "PreconditionCheck", "GitStatusCheck", "BranchCheck",
            "WriteVersionsAndTag", "ComposeRepoSync", "DockerBuild", "BuildComplete"
        })
        {
            Assert.IsTrue(names.Contains(expected), $"Missing step: {expected}");
        }
    }
}
