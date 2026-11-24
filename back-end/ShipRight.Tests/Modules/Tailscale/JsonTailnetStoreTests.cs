using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Tailscale;
using ShipRight.Shared.Tenancy;

namespace ShipRight.Tests.Modules.Tailscale;

[TestClass]
public class JsonTailnetStoreTests
{
    private sealed class Context(Guid company, bool system = false) : ICompanyContext
    {
        public Guid? CompanyId => company;
        public bool IsSystem => system;
        public Guid RequireCompanyId() => company;
        public IDisposable BeginSystemScope() => new Scope();
        private sealed class Scope : IDisposable { public void Dispose() { } }
    }

    [TestMethod]
    public async Task SecretsAreEncryptedAtRestAndRevealedOnlyThroughTheStore()
    {
        var dir = Path.Combine(Path.GetTempPath(), "shipright-tailnet-" + Guid.NewGuid().ToString("N"));
        var company = Guid.NewGuid();
        var store = new JsonTailnetStore(dir, new Context(company));

        await store.SaveAsync(new TailnetConnection
        {
            Id = "tailnet-1",
            Name = "Acme",
            Tailnet = "-",
            AuthKey = "tskey-secret-value",
            OAuthClientSecret = "oauth-secret-value",
        });

        var raw = File.ReadAllText(Path.Combine(dir, "tailnets.json"));
        Assert.IsFalse(raw.Contains("tskey-secret-value"), "the auth key must not be stored in plaintext");
        Assert.IsFalse(raw.Contains("oauth-secret-value"), "the OAuth secret must not be stored in plaintext");

        var read = await store.GetByIdAsync("tailnet-1");
        Assert.IsNotNull(read);
        Assert.AreEqual("tskey-secret-value", read.AuthKey);
        Assert.AreEqual("oauth-secret-value", read.OAuthClientSecret);

        Directory.Delete(dir, recursive: true);
    }

    [TestMethod]
    public async Task AnotherCompanyCannotReadTheTailnet()
    {
        var dir = Path.Combine(Path.GetTempPath(), "shipright-tailnet-" + Guid.NewGuid().ToString("N"));
        var owner = new JsonTailnetStore(dir, new Context(Guid.NewGuid()));
        await owner.SaveAsync(new TailnetConnection { Id = "tailnet-1", Name = "Acme", AuthKey = "key" });

        var other = new JsonTailnetStore(dir, new Context(Guid.NewGuid()));
        Assert.IsNull(await other.GetByIdAsync("tailnet-1"));

        Directory.Delete(dir, recursive: true);
    }
}
