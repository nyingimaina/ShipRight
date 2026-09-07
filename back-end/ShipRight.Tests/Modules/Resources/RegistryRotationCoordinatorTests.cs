using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Builds;
using ShipRight.Modules.Projects;
using ShipRight.Modules.Resources;
using ShipRight.Modules.Resources.Models;
using ShipRight.Modules.Resources.Stores;
using ShipRight.Shared.ProcessRunner;

namespace ShipRight.Tests.Modules.Resources;

[TestClass]
public class RegistryRotationCoordinatorTests
{
    private const string EcrHost = "123456789012.dkr.ecr.us-east-1.amazonaws.com";
    private const string ImageName = "123456789012.dkr.ecr.us-east-1.amazonaws.com/ship-right/app";
    private const string Repository = "ship-right/app";

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

    private sealed class FakeProfileStore : IAwsProfileResourceStore
    {
        private readonly Dictionary<Guid, AwsProfileResource> _items = [];
        public int Count => _items.Count;
        public void Seed(AwsProfileResource profile) => _items[profile.Id] = profile;
        public Task<List<AwsProfileResource>> GetAllAsync() => Task.FromResult(_items.Values.ToList());
        public Task<AwsProfileResource?> GetByIdAsync(Guid id) =>
            Task.FromResult(_items.TryGetValue(id, out var p) ? p : null);
        public Task<AwsProfileResource?> GetByNameAsync(string name) =>
            Task.FromResult(_items.Values.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)));
        public Task SaveAsync(AwsProfileResource resource) { _items[resource.Id] = resource; return Task.CompletedTask; }
        public Task DeleteAsync(Guid id) { _items.Remove(id); return Task.CompletedTask; }
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

    private static string DescribeJson(params (string Tag, long Millis)[] images)
    {
        var details = string.Join(",",
            images.Select(img =>
            $"{{\"imageTags\":[\"{img.Tag}\"],\"imageDigest\":\"sha256:{img.Tag}\",\"imagePushedAt\":{img.Millis}}}"));
        return $"{{\"imageDetails\":[{details}]}}";
    }

    private static ServiceConfig EcrService(int? retention = 3) => new()
    {
        Name = "api",
        DockerImageName = ImageName,
        ImageRetentionCount = retention,
    };

    private static DockerRegistryResource EcrResource(Guid? profileId = null) => new()
    {
        Registry = EcrHost,
        AuthType = RegistryAuthType.AwsEcr,
        AwsRegion = "us-east-1",
        AwsProfileResourceId = profileId,
    };

    private static ServiceVersion ShippedVersion(string version = "4.3.6") =>
        new() { ServiceName = "api", NewVersion = version };

    [TestMethod]
    public void ExtractRepositoryName_EcrImageWithHostPrefix_ReturnsRepoPath()
    {
        Assert.AreEqual("ship-right/app", RegistryRotationCoordinator.ExtractRepositoryName(EcrHost, ImageName));
    }

    [TestMethod]
    public void ExtractRepositoryName_BareDockerHubStyleImage_ReturnsWholeImage()
    {
        Assert.AreEqual("org/app", RegistryRotationCoordinator.ExtractRepositoryName("docker.io", "org/app"));
    }

    [TestMethod]
    public async Task PruneAfterPushAsync_NonEcrRegistry_SkipsWithoutAwsCalls()
    {
        var runner = new FakeRunner();
        var svc = new ServiceConfig { Name = "api", DockerImageName = "ghcr.io/org/app", ImageRetentionCount = 3 };
        var coordinator = new RegistryRotationCoordinator(runner, null, null);
        var log = new List<string>();

        await coordinator.PruneAfterPushAsync("p1", svc, ShippedVersion(), null, l => { log.Add(l); return Task.CompletedTask; });

        Assert.AreEqual(0, runner.Calls.Count);
        Assert.IsTrue(log.Any(l => l.Contains("not Amazon ECR")));
    }

    [TestMethod]
    public async Task PruneAfterPushAsync_KeepAll_DoesNotTouchEcr()
    {
        var runner = new FakeRunner();
        var svc = EcrService(retention: 0);
        var coordinator = new RegistryRotationCoordinator(runner, null, null);
        var log = new List<string>();

        await coordinator.PruneAfterPushAsync("p1", svc, ShippedVersion(), EcrResource(), l => { log.Add(l); return Task.CompletedTask; });

        Assert.AreEqual(0, runner.Calls.Count);
        Assert.IsTrue(log.Any(l => l.Contains("keep-all")));
    }

    [TestMethod]
    public async Task PruneAfterPushAsync_KeepsNewestCountAndProtects_DeletesOlderTag()
    {
        var runner = new FakeRunner(
            (["ecr", "get-lifecycle-policy"],
                new ProcessResult(254, "", "Lifecycle policy is not currently configured for this repository", TimeSpan.Zero)),
            (["ecr", "describe-images"],
                new ProcessResult(0, DescribeJson(
                    ("4.3.9", 9000), ("4.3.8", 8000), ("4.3.7", 7000), ("4.3.6", 6000),
                    ("4.3.5", 5000), ("4.3.4", 4000), ("4.3.3", 3000), ("4.3.2", 2000)), "", TimeSpan.Zero)),
            (["ecr", "batch-delete-image"],
                new ProcessResult(0, @"{""imageIds"":[{}],""failures"":[]}", "", TimeSpan.Zero)));
        var store = new FakeBuildStore();
        await store.SaveAsync(new BuildRecord
        {
            ProjectId = "p1",
            Status = BuildStatus.PushSucceeded,
            Versions = [new ServiceVersion { ServiceName = "api", NewVersion = "4.3.5" }],
        });
        var coordinator = new RegistryRotationCoordinator(runner, null, store);
        var log = new List<string>();

        await coordinator.PruneAfterPushAsync("p1", EcrService(retention: 3), ShippedVersion("4.3.9"), EcrResource(),
            l => { log.Add(l); return Task.CompletedTask; });

        // Lifecycle policy put (none present) + describe + batch delete
        var put = runner.Calls.Single(c => c.Args.Contains("put-lifecycle-policy"));
        var putIdx = Array.FindIndex(put.Args, a => a == "--lifecycle-policy-text");
        Assert.IsTrue(put.Args[putIdx + 1].Contains("\"countNumber\":3"));

        var describe = runner.Calls.First(c => c.Args.Contains("describe-images"));
        Assert.IsNotNull(describe);

        var deletes = runner.Calls.Where(c => c.Args.Contains("batch-delete-image")).ToList();
        Assert.AreEqual(1, deletes.Count);
        CollectionAssert.Contains(deletes[0].Args, "imageTag=4.3.2");
        CollectionAssert.Contains(deletes[0].Args, "imageTag=4.3.4");

        Assert.IsTrue(log.Any(l => l.Contains("pruned ship-right/app:4.3.2")));
    }

    [TestMethod]
    public async Task PruneAfterPushAsync_ExistingPolicy_DoesNotPutAgain()
    {
        var runner = new FakeRunner(
            (["ecr", "get-lifecycle-policy"],
                new ProcessResult(0, @"{""lifecyclePolicyText"":""{\""rules\"":[]}""}", "", TimeSpan.Zero)),
            (["ecr", "describe-images"],
                new ProcessResult(0, DescribeJson(("4.3.6", 6000), ("4.3.5", 5000)), "", TimeSpan.Zero)));
        var coordinator = new RegistryRotationCoordinator(runner, null, null);
        var log = new List<string>();

        await coordinator.PruneAfterPushAsync("p1", EcrService(retention: 3), ShippedVersion(), EcrResource(),
            l => { log.Add(l); return Task.CompletedTask; });

        Assert.IsFalse(runner.Calls.Any(c => c.Args.Contains("put-lifecycle-policy")));
        Assert.IsTrue(log.Any(l => l.Contains("already present")));
    }

    [TestMethod]
    public async Task PruneAfterPushAsync_ManyTags_ChunksBatchDeleteAtHundred()
    {
        var images = Enumerable.Range(0, 250)
            .Select(i => (Tag: $"v{i}", Millis: (long)(250 - i)))
            .ToArray();
        var runner = new FakeRunner(
            (["ecr", "describe-images"],
                new ProcessResult(0, DescribeJson(images), "", TimeSpan.Zero)),
            (["ecr", "batch-delete-image"],
                new ProcessResult(0, @"{""failures"":[]}", "", TimeSpan.Zero)));
        var coordinator = new RegistryRotationCoordinator(runner, null, null);
        var log = new List<string>();

        await coordinator.PruneAfterPushAsync("p1", EcrService(retention: 5), ShippedVersion("v0"), EcrResource(),
            l => { log.Add(l); return Task.CompletedTask; });

        var deletes = runner.Calls.Where(c => c.Args.Contains("batch-delete-image")).ToList();
        Assert.AreEqual(3, deletes.Count);
        Assert.AreEqual(100, deletes[0].Args.Count(a => a.StartsWith("imageTag=")));
        Assert.AreEqual(100, deletes[1].Args.Count(a => a.StartsWith("imageTag=")));
        Assert.AreEqual(44, deletes[2].Args.Count(a => a.StartsWith("imageTag=")));
    }

    [TestMethod]
    public async Task PruneAfterPushAsync_FailedDescribe_LogsAndDoesNotThrow()
    {
        var runner = new FakeRunner(
            fallback: new ProcessResult(255, "", "describe failed", TimeSpan.Zero));
        var coordinator = new RegistryRotationCoordinator(runner, null, null);
        var log = new List<string>();

        await coordinator.PruneAfterPushAsync("p1", EcrService(), ShippedVersion(), EcrResource(),
            l => { log.Add(l); return Task.CompletedTask; });

        Assert.IsTrue(log.Any(l => l.Contains("Registry retention failed")));
    }

    [TestMethod]
    public async Task PruneAfterPushAsync_UsesBoundProfileEnvironment()
    {
        var profileId = Guid.NewGuid();
        var profile = new AwsProfileResource { Id = profileId, Name = "prod", ProfileName = "shipright-prod", DefaultRegion = "eu-west-1" };
        var profiles = new FakeProfileStore();
        profiles.Seed(profile);

        var runner = new FakeRunner(
            (["ecr", "describe-images"],
                new ProcessResult(0, DescribeJson(("4.3.6", 6000), ("4.3.5", 5000)), "", TimeSpan.Zero)));
        var coordinator = new RegistryRotationCoordinator(runner, profiles, null);
        var log = new List<string>();

        await coordinator.PruneAfterPushAsync("p1", EcrService(), ShippedVersion(), EcrResource(profileId),
            l => { log.Add(l); return Task.CompletedTask; });

        var firstCall = runner.Calls[0];
        Assert.IsNotNull(firstCall.Env);
        Assert.AreEqual("shipright-prod", firstCall.Env!["AWS_PROFILE"]);
    }
}