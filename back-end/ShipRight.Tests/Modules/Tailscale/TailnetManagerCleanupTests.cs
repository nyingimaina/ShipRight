using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.ServerNetwork;
using ShipRight.Modules.Tailscale;

namespace ShipRight.Tests.Modules.Tailscale;

[TestClass]
public class TailnetManagerCleanupTests
{
    private sealed class EmptyStore : ITailnetStore
    {
        public Task<List<TailnetConnection>> GetAllAsync() => Task.FromResult(new List<TailnetConnection>());
        public Task<TailnetConnection?> GetByIdAsync(string id) => Task.FromResult<TailnetConnection?>(null);
        public Task SaveAsync(TailnetConnection connection) => Task.CompletedTask;
        public Task DeleteAsync(string id) => Task.CompletedTask;
    }

    [TestMethod]
    public async Task RemoveAsync_DeletesTheStateKeptForThatNetworkOnly()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "shipright-tailnet-clean-" + Guid.NewGuid().ToString("N"));
        var company = Guid.NewGuid().ToString("N");
        var removed = Path.Combine(dataDir, "tailscale", company, "removed-network");
        var kept = Path.Combine(dataDir, "tailscale", company, "other-network");
        Directory.CreateDirectory(removed);
        Directory.CreateDirectory(kept);
        await File.WriteAllTextAsync(Path.Combine(removed, "ts.state"), "state");
        await File.WriteAllTextAsync(Path.Combine(kept, "ts.state"), "state");

        var manager = new TailnetManager(new EmptyStore(), new TailscaleOAuthKeyMinter(), dataDir: dataDir);

        await manager.RemoveAsync("removed-network");

        Assert.IsFalse(Directory.Exists(removed), "the removed network's state must not be left on disk");
        Assert.IsTrue(Directory.Exists(kept), "another network's state must be untouched");

        Directory.Delete(dataDir, recursive: true);
    }
}
