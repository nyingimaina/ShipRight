using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Builds;
using ShipRight.Modules.Projects;
using ShipRight.Modules.Resources;
using ShipRight.Shared.ProcessRunner;

namespace ShipRight.Tests.Modules.Resources;

[TestClass]
public class BuildMachineImagePrunerTests
{
    private const string ImageName = "080632633137.dkr.ecr.us-east-2.amazonaws.com/ship-right/app";

    private sealed class FakeRunner : IProcessRunner
    {
        private readonly (string[] Tokens, ProcessResult Result)[] _responses;
        private readonly ProcessResult _fallback;

        public List<(string[] Args, IReadOnlyDictionary<string, string>? Env)> Calls { get; } = [];

        public FakeRunner(params (string[] Tokens, ProcessResult Result)[] responses)
            : this(null, responses)
        {
        }

        public FakeRunner(ProcessResult? fallback, params (string[] Tokens, ProcessResult Result)[] responses)
        {
            _fallback = fallback ?? new ProcessResult(0, "", "", TimeSpan.Zero);
            _responses = responses;
        }

        public Task<ProcessResult> RunAsync(
            string executable, string[] args, string? workingDir,
            Func<string, Task>? onOutput = null, Func<string, Task>? onError = null,
            CancellationToken ct = default, IReadOnlyDictionary<string, string>? envOverride = null,
            TimeSpan? timeout = null, string? stdin = null)
        {
            Calls.Add((args, envOverride));
            foreach (var (tokens, result) in _responses)
                if (tokens.All(t => args.Contains(t)))
                    return Task.FromResult(result);
            return Task.FromResult(_fallback);
        }
    }

    private sealed class FakeBuildStore : IBuildStore
    {
        public List<BuildRecord> Records { get; } = [];
        public int Count => Records.Count;
        public Task SaveAsync(BuildRecord record) { Records.Insert(0, record); return Task.CompletedTask; }
        public Task<BuildRecord?> GetByIdAsync(string id) =>
            Task.FromResult(Records.FirstOrDefault(r => r.Id == id));
        public Task<List<BuildRecord>> QueryAsync(string? projectId, string? status, DateTime? from, DateTime? to,
            string? gitTag, int page, int pageSize) =>
            Task.FromResult(Records.Where(r => projectId is null || r.ProjectId == projectId).ToList());
        public Task<int> CountQueryAsync(string? projectId, string? status, DateTime? from, DateTime? to, string? gitTag) =>
            Task.FromResult(Records.Count);
        public Task MarkInterruptedAsync() => Task.CompletedTask;
    }

    private static ServiceConfig Service(int? keepCount = 2) => new()
    {
        Name = "api",
        DockerImageName = ImageName,
        LocalImageKeepCount = keepCount,
    };

    private static ServiceVersion ShippedVersion(string version = "4.3.5") =>
        new() { ServiceName = "api", NewVersion = version };

    private static ProjectConfig Project(int cacheKeepGb = 5) => new()
    {
        Id = "p1",
        Name = "Test",
        LocalCachePruneKeepGb = cacheKeepGb,
    };

    [TestMethod]
    public async Task PruneLocalImages_RemovesUnusedOlderImages_KeepsInUseAndCurrent()
    {
        var runner = new FakeRunner(
            (["images"], new ProcessResult(0, $"{ImageName}:4.3.5\n{ImageName}:4.3.4\n{ImageName}:4.3.3", "", TimeSpan.Zero)),
            (["ps"], new ProcessResult(0, $"{ImageName}:4.3.4", "", TimeSpan.Zero)),
            (["system", "df"], new ProcessResult(0, "Images          3         1         682.4MB       682.4MB (100%)", "", TimeSpan.Zero)),
            (["image", "prune"], new ProcessResult(0, "Total reclaimed space: 12MB", "", TimeSpan.Zero)),
            (["builder"], new ProcessResult(0, "Total reclaimed space: 45MB", "", TimeSpan.Zero)));
        var pruner = new BuildMachineImagePruner(runner, null);
        var log = new List<string>();

        await pruner.PruneLocalImagesAsync(Project(), Service(), ShippedVersion(),
            l => { log.Add(l); return Task.CompletedTask; });

        var rmiCalls = runner.Calls.Where(c => c.Args.Contains("rmi")).ToList();
        Assert.AreEqual(1, rmiCalls.Count);
        CollectionAssert.Contains(rmiCalls[0].Args, $"{ImageName}:4.3.3");
        Assert.IsTrue(log.Any(l => l.Contains("removed ")));

        Assert.AreEqual(2, runner.Calls.Count(c => c.Args.Contains("system") && c.Args.Contains("df")));
        Assert.IsTrue(runner.Calls.Any(c => c.Args.Contains("image") && c.Args.Contains("prune")));
        var builderPrune = runner.Calls.First(c => c.Args.Contains("builder"));
        CollectionAssert.Contains(builderPrune.Args, "--keep-storage");
        CollectionAssert.Contains(builderPrune.Args, "5gb");
    }

    [TestMethod]
    public async Task PruneLocalImages_KeepCountZero_DeletesAllUnused()
    {
        var runner = new FakeRunner(
            (["images"], new ProcessResult(0, $"{ImageName}:4.3.5\n{ImageName}:4.3.4\n{ImageName}:4.3.3\n{ImageName}:4.3.2", "", TimeSpan.Zero)),
            (["ps"], new ProcessResult(0, $"{ImageName}:4.3.4", "", TimeSpan.Zero)),
            (["system", "df"], new ProcessResult(0, "Images          4         1         900MB        800MB (89%)", "", TimeSpan.Zero)),
            (["image", "prune"], new ProcessResult(0, "Total reclaimed space: 1MB", "", TimeSpan.Zero)));
        var pruner = new BuildMachineImagePruner(runner, null);
        var log = new List<string>();

        await pruner.PruneLocalImagesAsync(Project(), Service(keepCount: 0), ShippedVersion(),
            l => { log.Add(l); return Task.CompletedTask; });

        var removed = runner.Calls
            .Where(c => c.Args.Contains("rmi"))
            .Select(c => c.Args.Last())
            .ToList();
        CollectionAssert.AreEquivalent(new[] { $"{ImageName}:4.3.3", $"{ImageName}:4.3.2" }, removed);
    }

    [TestMethod]
    public async Task PruneLocalImages_KeepWindowFromBuildHistory_KeepsRecentPushedVersions()
    {
        var store = new FakeBuildStore();
        await store.SaveAsync(new BuildRecord
        {
            ProjectId = "p1",
            Status = BuildStatus.PushSucceeded,
            Versions = [new ServiceVersion { ServiceName = "api", NewVersion = "4.3.5" }],
        });
        var runner = new FakeRunner(
            (["images"], new ProcessResult(0, $"{ImageName}:4.3.6\n{ImageName}:4.3.5\n{ImageName}:4.3.4\n{ImageName}:4.3.3", "", TimeSpan.Zero)),
            (["ps"], new ProcessResult(0, "", "", TimeSpan.Zero)),
            (["system", "df"], new ProcessResult(0, "Images          4         0         900MB        800MB (89%)", "", TimeSpan.Zero)),
            (["image", "prune"], new ProcessResult(0, "Total reclaimed space: 1MB", "", TimeSpan.Zero)));
        var pruner = new BuildMachineImagePruner(runner, store);
        var log = new List<string>();

        await pruner.PruneLocalImagesAsync(Project(), Service(), ShippedVersion("4.3.6"),
            l => { log.Add(l); return Task.CompletedTask; });

        var removed = runner.Calls
            .Where(c => c.Args.Contains("rmi"))
            .Select(c => c.Args.Last())
            .ToList();
        CollectionAssert.AreEquivalent(new[] { $"{ImageName}:4.3.4", $"{ImageName}:4.3.3" }, removed);
    }

    [TestMethod]
    public async Task PruneLocalImages_CacheKeepZero_SkipsBuilderPrune()
    {
        var runner = new FakeRunner(
            (["images"], new ProcessResult(0, $"{ImageName}:4.3.5", "", TimeSpan.Zero)),
            (["ps"], new ProcessResult(0, "", "", TimeSpan.Zero)),
            (["system", "df"], new ProcessResult(0, "Images          1         0         10MB        10MB (100%)", "", TimeSpan.Zero)),
            (["image", "prune"], new ProcessResult(0, "Total reclaimed space: 1MB", "", TimeSpan.Zero)));
        var pruner = new BuildMachineImagePruner(runner, null);
        var log = new List<string>();

        await pruner.PruneLocalImagesAsync(Project(cacheKeepGb: 0), Service(), ShippedVersion(),
            l => { log.Add(l); return Task.CompletedTask; });

        Assert.IsFalse(runner.Calls.Any(c => c.Args.Contains("builder")));
    }

    [TestMethod]
    public async Task PruneLocalImages_NoLocalImages_SkipsRemoval()
    {
        var runner = new FakeRunner(
            (["images"], new ProcessResult(0, "", "", TimeSpan.Zero)));
        var pruner = new BuildMachineImagePruner(runner, null);
        var log = new List<string>();

        await pruner.PruneLocalImagesAsync(Project(), Service(), ShippedVersion(),
            l => { log.Add(l); return Task.CompletedTask; });

        Assert.AreEqual(0, runner.Calls.Count(c => c.Args.Contains("rmi")));
        Assert.IsTrue(log.Any(l => l.Contains("no local images")));
    }
}