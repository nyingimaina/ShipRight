using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Builds;
using ShipRight.Modules.Projects;
using ShipRight.Modules.Resources;
using ShipRight.Shared.ProcessRunner;

namespace ShipRight.Tests.Modules.Resources;

[TestClass]
public class PushAwareImagePrunerTests
{
    private const string Image = "shop/api";

    private sealed class ScriptedRunner : IProcessRunner
    {
        private readonly Dictionary<string, ProcessResult> _byToken = [];
        public List<string[]> Calls { get; } = [];

        public ScriptedRunner Reply(string token, string stdout = "", int exit = 0)
        {
            _byToken[token] = new ProcessResult(exit, stdout, "", TimeSpan.Zero);
            return this;
        }

        public Task<ProcessResult> RunAsync(string executable, string[] args, string? workingDir,
            Func<string, Task>? onOutput = null, Func<string, Task>? onError = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? envOverride = null, TimeSpan? timeout = null, string? stdin = null)
        {
            Calls.Add(args);
            foreach (var (token, result) in _byToken)
                if (args.Contains(token)) return Task.FromResult(result);
            return Task.FromResult(new ProcessResult(0, "", "", TimeSpan.Zero));
        }

        public IEnumerable<string> Removed => Calls.Where(c => c.Contains("rmi")).Select(c => c.Last());
    }

    private static ServiceConfig Svc(int? keep = 0, bool? skip = null) =>
        new() { Name = "api", DockerImageName = Image, LocalImageKeepCount = keep, SkipLocalCleanup = skip };

    private static ServiceVersion Shipped(string v = "1.0.1") => new() { ServiceName = "api", NewVersion = v };

    private static (PushAwareImagePruner Pruner, ScriptedRunner Runner, PushLedger Ledger) Build(
        string imagesOutput, string inUse = "")
    {
        var runner = new ScriptedRunner().Reply("images", imagesOutput).Reply("ps", inUse);
        var ledger = new PushLedger();
        return (new PushAwareImagePruner(runner, null, ledger), runner, ledger);
    }

    private static Task Prune(PushAwareImagePruner p, ServiceConfig svc, ServiceVersion sv, List<string>? log = null) =>
        p.PruneLocalImagesAsync(new ProjectConfig { Id = "p1" }, svc, sv, l => { log?.Add(l); return Task.CompletedTask; });

    [TestMethod]
    public async Task KeepZero_AfterASuccessfulPush_RemovesTheImageJustShipped()
    {
        var (pruner, runner, ledger) = Build($"{Image}:1.0.1\n{Image}:1.0.0");
        ledger.RecordPush($"{Image}:1.0.1");

        await Prune(pruner, Svc(), Shipped());

        CollectionAssert.Contains(runner.Removed.ToList(), $"{Image}:1.0.1");
        CollectionAssert.Contains(runner.Removed.ToList(), $"{Image}:1.0.0");
    }

    [TestMethod]
    public async Task KeepZero_WhenNothingWasPushed_KeepsTheImageBecauseItIsTheOnlyCopy()
    {
        var (pruner, runner, _) = Build($"{Image}:1.0.1\n{Image}:1.0.0");

        await Prune(pruner, Svc(), Shipped());

        CollectionAssert.DoesNotContain(runner.Removed.ToList(), $"{Image}:1.0.1");
    }

    [TestMethod]
    public async Task KeepZero_NeverRemovesAnImageARunningContainerUses()
    {
        var (pruner, runner, ledger) = Build($"{Image}:1.0.1", inUse: $"{Image}:1.0.1");
        ledger.RecordPush($"{Image}:1.0.1");

        await Prune(pruner, Svc(), Shipped());

        CollectionAssert.DoesNotContain(runner.Removed.ToList(), $"{Image}:1.0.1");
    }

    [TestMethod]
    public async Task KeepTwo_StillKeepsTheShippedImage_EvenAfterAPush()
    {
        var (pruner, runner, ledger) = Build($"{Image}:1.0.1");
        ledger.RecordPush($"{Image}:1.0.1");

        await Prune(pruner, Svc(keep: 2), Shipped());

        CollectionAssert.DoesNotContain(runner.Removed.ToList(), $"{Image}:1.0.1");
    }

    [TestMethod]
    public async Task OptedOutService_IsNotTouchedAtAll()
    {
        var (pruner, runner, ledger) = Build($"{Image}:1.0.1\n{Image}:1.0.0");
        ledger.RecordPush($"{Image}:1.0.1");
        var log = new List<string>();

        await Prune(pruner, Svc(skip: true), Shipped(), log);

        Assert.AreEqual(0, runner.Calls.Count, "no docker command may run for an opted-out service");
        Assert.IsTrue(log.Any(l => l.Contains("opted out") || l.Contains("skipped")));
    }

    [TestMethod]
    public async Task OnlyTheServicesOwnPushedTagIsRemoved_NotAnotherServicesUnpushedImage()
    {
        var (pruner, runner, ledger) = Build($"{Image}:1.0.1");
        ledger.RecordPush("shop/web:2.0.0"); // a different service pushed; this one has not

        await Prune(pruner, Svc(), Shipped());

        CollectionAssert.DoesNotContain(runner.Removed.ToList(), $"{Image}:1.0.1");
    }

    [TestMethod]
    public async Task ARemovedImage_IsForgottenByTheLedger()
    {
        var (pruner, _, ledger) = Build($"{Image}:1.0.1");
        ledger.RecordPush($"{Image}:1.0.1");

        await Prune(pruner, Svc(), Shipped());

        Assert.IsFalse(ledger.WasPushed($"{Image}:1.0.1"));
    }

    [TestMethod]
    public async Task ARunnerThatFailsToRemove_DoesNotThrow()
    {
        var (pruner, runner, ledger) = Build($"{Image}:1.0.1");
        runner.Reply("rmi", exit: 1);
        ledger.RecordPush($"{Image}:1.0.1");

        await Prune(pruner, Svc(), Shipped());

        Assert.IsTrue(ledger.WasPushed($"{Image}:1.0.1"), "a failed removal is not forgotten");
    }
}

[TestClass]
public class PushRecordingProcessRunnerTests
{
    private sealed class Inner(int exit) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string executable, string[] args, string? workingDir,
            Func<string, Task>? onOutput = null, Func<string, Task>? onError = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? envOverride = null, TimeSpan? timeout = null, string? stdin = null) =>
            Task.FromResult(new ProcessResult(exit, "out", "", TimeSpan.Zero));
    }

    [TestMethod]
    public async Task SuccessfulDockerPush_IsRecorded_AndTheResultPassesThrough()
    {
        var ledger = new PushLedger();
        var runner = new PushRecordingProcessRunner(new Inner(0), ledger);

        var result = await runner.RunAsync("docker", ["push", "shop/api:1.0.1"], null);

        Assert.AreEqual("out", result.StdOut);
        Assert.IsTrue(ledger.WasPushed("shop/api:1.0.1"));
    }

    [TestMethod]
    public async Task FailedPush_IsNotRecorded()
    {
        var ledger = new PushLedger();
        await new PushRecordingProcessRunner(new Inner(1), ledger).RunAsync("docker", ["push", "shop/api:1.0.1"], null);

        Assert.IsFalse(ledger.WasPushed("shop/api:1.0.1"));
    }

    [TestMethod]
    public async Task OtherCommands_AreNotRecorded()
    {
        var ledger = new PushLedger();
        var runner = new PushRecordingProcessRunner(new Inner(0), ledger);

        await runner.RunAsync("git", ["push", "origin", "main"], null);
        await runner.RunAsync("docker", ["build", "-t", "shop/api:1.0.1", "."], null);

        Assert.IsFalse(ledger.WasPushed("origin"));
        Assert.IsFalse(ledger.WasPushed("shop/api:1.0.1"));
    }
}
