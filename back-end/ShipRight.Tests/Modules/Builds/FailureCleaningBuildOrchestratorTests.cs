using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Builds;
using ShipRight.Modules.Projects;
using ShipRight.Modules.Resources;
using ShipRight.Shared.Events;
using ShipRight.Tests.Modules.Setup;

namespace ShipRight.Tests.Modules.Builds;

[TestClass]
public class FailureCleaningBuildOrchestratorTests
{
    private sealed class Testable : FailureCleaningBuildOrchestrator
    {
        public Testable(RecordingRunner runner)
            : base(null!, null!, new BuildEventBus(), runner, null!, null!, null!, null!, null!, null!,
                localImagePruner: new PushAwareImagePruner(runner, null, new PushLedger())) { }

        public Task Build(BuildRecord record, ProjectConfig project) =>
            RunDockerBuildAsync(new PipelineContext(record, new BuildEventBus()), record, project, () => Task.CompletedTask);
    }

    private static ProjectConfig Project(bool skip = false, int cacheGb = 5) => new()
    {
        Id = "p1", Name = "Shop", LocalCachePruneKeepGb = cacheGb,
        Services = [new ServiceConfig { Name = "Api", BuildContextPath = @"D:\work\shop\Api", DockerImageName = "shop/api", SkipLocalCleanup = skip }],
    };

    private static BuildRecord Record() => new()
    {
        Id = "b1", ProjectId = "p1",
        Versions = [new ServiceVersion { ServiceName = "Api", NewVersion = "1.0.1", DockerImageName = "shop/api" }],
    };

    private static RecordingRunner BuildFails() =>
        new RecordingRunner()
            .When((e, a) => e == "docker" && a.Contains("inspect"), exit: 1)
            .When((e, a) => e == "docker" && a.Length > 0 && a[0] == "build", exit: 1);

    [TestMethod]
    public async Task AFailedDockerBuild_ClearsTheLeftoverLayersAndTrimsTheCache_ThenStillFails()
    {
        var runner = BuildFails();

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => new Testable(runner).Build(Record(), Project()));

        Assert.IsTrue(runner.Any("image prune -f"), "dangling layers from the failed build are removed");
        Assert.IsTrue(runner.Any("builder prune -f --keep-storage 5gb"));
    }

    [TestMethod]
    public async Task ASuccessfulBuild_DoesNotRunTheFailureClean()
    {
        var runner = new RecordingRunner().When((e, a) => e == "docker" && a.Contains("inspect"), exit: 1);

        await new Testable(runner).Build(Record(), Project());

        Assert.IsFalse(runner.Any("prune"));
    }

    [TestMethod]
    public async Task AProjectWhoseServicesAllOptedOut_IsNotTouchedEvenWhenABuildFails()
    {
        var runner = BuildFails();

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => new Testable(runner).Build(Record(), Project(skip: true)));

        Assert.IsFalse(runner.Any("prune"));
    }

    [TestMethod]
    public async Task AFailingClean_NeverHidesTheRealBuildError()
    {
        var runner = BuildFails().WhenFirst((e, a) => e == "docker" && a.Contains("prune"), exit: 1);

        var ex = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => new Testable(runner).Build(Record(), Project()));

        StringAssert.Contains(ex.Message, "docker build failed");
    }

    [TestMethod]
    public async Task ACacheBudgetOfZero_MeansTheCacheIsNeverTrimmed()
    {
        var runner = BuildFails();

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => new Testable(runner).Build(Record(), Project(cacheGb: 0)));

        Assert.IsTrue(runner.Any("image prune -f"));
        Assert.IsFalse(runner.Any("builder prune"));
    }

    [TestMethod]
    public async Task AnImageThatFailedToBuild_IsNeverTouchedByTheFailureClean()
    {
        // The failed build's other images stay: they are what lets a retry skip rebuilding. Only garbage goes.
        var runner = BuildFails();

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => new Testable(runner).Build(Record(), Project()));

        Assert.IsFalse(runner.Any(" rmi "));
    }
}
