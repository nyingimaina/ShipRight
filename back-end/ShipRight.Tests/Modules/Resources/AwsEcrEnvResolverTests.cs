using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Resources;
using ShipRight.Modules.Resources.Models;
using ShipRight.Modules.Resources.Stores;

namespace ShipRight.Tests.Modules.Resources;

[TestClass]
public class AwsEcrEnvResolverTests
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
            Task.FromResult(_items.Values.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)));
        public Task SaveAsync(AwsProfileResource resource) { _items[resource.Id] = resource; return Task.CompletedTask; }
        public Task DeleteAsync(Guid id) { _items.Remove(id); return Task.CompletedTask; }
    }

    [TestMethod]
    public async Task Resolve_NamedProfile_SetsProfileAndRegion()
    {
        var profile = new AwsProfileResource { Id = Guid.NewGuid(), Name = "prod", ProfileName = "shipright-prod", DefaultRegion = "eu-west-1" };
        var store = new FakeProfileStore();
        store.Seed(profile);
        var resource = new DockerRegistryResource { AwsProfileResourceId = profile.Id };

        var env = await AwsEcrEnvResolver.ResolveAsync(store, resource);

        Assert.IsNotNull(env);
        Assert.AreEqual("shipright-prod", env["AWS_PROFILE"]);
        Assert.AreEqual("eu-west-1", env["AWS_DEFAULT_REGION"]);
    }

    [TestMethod]
    public async Task Resolve_ExplicitKeys_SetsKeyEnvironment()
    {
        var profile = new AwsProfileResource
        {
            Id = Guid.NewGuid(),
            Name = "keys",
            AccessKeyId = "AKIATESTKEYID",
            SecretAccessKey = "secret",
            SessionToken = "token-1",
        };
        var store = new FakeProfileStore();
        store.Seed(profile);
        var resource = new DockerRegistryResource { AwsProfileResourceId = profile.Id };

        var env = await AwsEcrEnvResolver.ResolveAsync(store, resource);

        Assert.IsNotNull(env);
        Assert.AreEqual("AKIATESTKEYID", env["AWS_ACCESS_KEY_ID"]);
        Assert.AreEqual("secret", env["AWS_SECRET_ACCESS_KEY"]);
        Assert.AreEqual("token-1", env["AWS_SESSION_TOKEN"]);
        Assert.IsFalse(env.ContainsKey("AWS_PROFILE"));
    }

    [TestMethod]
    public async Task Resolve_NoProfileBound_ReturnsNull()
    {
        var env = await AwsEcrEnvResolver.ResolveAsync(new FakeProfileStore(), new DockerRegistryResource());
        Assert.IsNull(env);
    }

    [TestMethod]
    public async Task Resolve_MissingProfile_Throws()
    {
        var store = new FakeProfileStore();
        var resource = new DockerRegistryResource { AwsProfileResourceId = Guid.NewGuid() };

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => AwsEcrEnvResolver.ResolveAsync(store, resource));
    }
}