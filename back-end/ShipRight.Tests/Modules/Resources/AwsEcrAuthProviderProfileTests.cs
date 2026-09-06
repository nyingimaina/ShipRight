using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Resources;
using ShipRight.Modules.Resources.Models;
using ShipRight.Modules.Resources.Stores;
using ShipRight.Shared.ProcessRunner;

namespace ShipRight.Tests.Modules.Resources;

[TestClass]
public class AwsEcrAuthProviderProfileTests
{
    private sealed class FakeProfileStore : IAwsProfileResourceStore
    {
        private readonly Dictionary<Guid, AwsProfileResource> _items = [];

        public int Count => _items.Count;
        public void Seed(AwsProfileResource profile) => _items[profile.Id] = profile;
        public Task<List<AwsProfileResource>> GetAllAsync() => Task.FromResult(_items.Values.ToList());
        public Task<AwsProfileResource?> GetByIdAsync(Guid id) =>
            Task.FromResult(_items.TryGetValue(id, out var p) ? p : null);
        public Task<AwsProfileResource?> GetByNameAsync(string name) =>
            Task.FromResult(_items.Values.FirstOrDefault(p =>
                p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)));
        public Task SaveAsync(AwsProfileResource resource) { _items[resource.Id] = resource; return Task.CompletedTask; }
        public Task DeleteAsync(Guid id) { _items.Remove(id); return Task.CompletedTask; }
    }

    private sealed class FakeProcessRunner : IProcessRunner
    {
        public IReadOnlyDictionary<string, string>? LastEnv { get; private set; }
        public ProcessResult Result { get; set; } = new(0, "ecr-token", "", TimeSpan.Zero);

        public Task<ProcessResult> RunAsync(
            string executable, string[] args, string? workingDir,
            Func<string, Task>? onOutput = null, Func<string, Task>? onError = null,
            CancellationToken ct = default, IReadOnlyDictionary<string, string>? envOverride = null,
            TimeSpan? timeout = null, string? stdin = null)
        {
            LastEnv = envOverride;
            return Task.FromResult(Result);
        }
    }

    private static DockerRegistryResource MakeEcrResource(Guid profileId) => new()
    {
        Registry = "123.dkr.ecr.us-east-1.amazonaws.com",
        AuthType = RegistryAuthType.AwsEcr,
        AwsRegion = "us-east-1",
        AwsProfileResourceId = profileId,
    };

    [TestMethod]
    public async Task NamedProfile_SetsAwsProfileEnvVar()
    {
        var guard = Guid.NewGuid();
        var profile = new AwsProfileResource
        {
            Id = guard,
            Name = "prod",
            ProfileName = "shipright-prod",
            DefaultRegion = "eu-west-1",
        };
        var store = new FakeProfileStore();
        store.Seed(profile);
        var runner = new FakeProcessRunner();
        var provider = new AwsEcrAuthProvider(runner, store);

        await provider.GetLoginCredentialsAsync(
            "123.dkr.ecr.us-east-1.amazonaws.com", MakeEcrResource(guard), "", "");

        Assert.IsNotNull(runner.LastEnv);
        Assert.AreEqual("shipright-prod", runner.LastEnv["AWS_PROFILE"]);
        Assert.AreEqual("eu-west-1", runner.LastEnv["AWS_DEFAULT_REGION"]);
    }

    [TestMethod]
    public async Task ExplicitKeys_SetsAccessKeyEnvVars()
    {
        var guard = Guid.NewGuid();
        var profile = new AwsProfileResource
        {
            Id = guard,
            Name = "prod",
            AccessKeyId = "AKIATESTKEYID",
            SecretAccessKey = "secret-key",
            SessionToken = "session-token-123",
        };
        var store = new FakeProfileStore();
        store.Seed(profile);
        var runner = new FakeProcessRunner();
        var provider = new AwsEcrAuthProvider(runner, store);

        await provider.GetLoginCredentialsAsync(
            "123.dkr.ecr.us-east-1.amazonaws.com", MakeEcrResource(guard), "", "");

        Assert.IsNotNull(runner.LastEnv);
        Assert.AreEqual("AKIATESTKEYID", runner.LastEnv["AWS_ACCESS_KEY_ID"]);
        Assert.AreEqual("secret-key", runner.LastEnv["AWS_SECRET_ACCESS_KEY"]);
        Assert.AreEqual("session-token-123", runner.LastEnv["AWS_SESSION_TOKEN"]);
    }

    [TestMethod]
    public async Task ExplicitKeys_WithoutSessionToken_OmitsIt()
    {
        var guard = Guid.NewGuid();
        var profile = new AwsProfileResource
        {
            Id = guard,
            Name = "prod",
            AccessKeyId = "AKIATESTKEYID",
            SecretAccessKey = "secret-key",
        };
        var store = new FakeProfileStore();
        store.Seed(profile);
        var runner = new FakeProcessRunner();
        var provider = new AwsEcrAuthProvider(runner, store);

        await provider.GetLoginCredentialsAsync(
            "123.dkr.ecr.us-east-1.amazonaws.com", MakeEcrResource(guard), "", "");

        Assert.IsNotNull(runner.LastEnv);
        Assert.IsFalse(runner.LastEnv.ContainsKey("AWS_SESSION_TOKEN"));
    }

    [TestMethod]
    public async Task MissingProfile_Throws()
    {
        var store = new FakeProfileStore();
        var runner = new FakeProcessRunner();
        var provider = new AwsEcrAuthProvider(runner, store);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            provider.GetLoginCredentialsAsync(
                "123.dkr.ecr.us-east-1.amazonaws.com",
                MakeEcrResource(Guid.NewGuid()), "", ""));
    }

    [TestMethod]
    public async Task NoProfileStore_ReturnsNullEnv_NoProfileEnvSet()
    {
        var runner = new FakeProcessRunner();
        var provider = new AwsEcrAuthProvider(runner);

        await provider.GetLoginCredentialsAsync(
            "123.dkr.ecr.us-east-1.amazonaws.com", null, "", "");

        Assert.IsNull(runner.LastEnv);
    }

    [TestMethod]
    public async Task NoBoundProfileId_ReturnsNullEnv()
    {
        var store = new FakeProfileStore();
        var runner = new FakeProcessRunner();
        var provider = new AwsEcrAuthProvider(runner, store);
        var resource = new DockerRegistryResource
        {
            Registry = "123.dkr.ecr.us-east-1.amazonaws.com",
            AuthType = RegistryAuthType.AwsEcr,
            AwsRegion = "us-east-1",
        };

        await provider.GetLoginCredentialsAsync(resource.Registry, resource, "", "");

        Assert.IsNull(runner.LastEnv);
    }
}