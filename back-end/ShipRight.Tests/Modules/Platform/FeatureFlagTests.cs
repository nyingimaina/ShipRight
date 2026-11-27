using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Platform;

namespace ShipRight.Tests.Modules.Platform;

[TestClass]
public class FeatureFlagTests
{
    private static FlagContext Ctx(
        string? tenant = null, string? company = null, string? project = null, string? user = null) =>
        new(tenant, company, project, user);

    [TestMethod]
    public async Task EverythingIsOffByDefault()
    {
        var store = new InMemoryFeatureFlagStore();

        Assert.IsFalse(await store.IsEnabledAsync("advisor.shadow", Ctx(project: "p1")));
        Assert.AreEqual(0, (await store.GetAllAsync()).Count);
    }

    [TestMethod]
    public async Task AGlobalFlagAppliesEverywhere()
    {
        var store = new InMemoryFeatureFlagStore();
        await store.SetAsync(new FeatureFlag("advisor.shadow", true));

        Assert.IsTrue(await store.IsEnabledAsync("advisor.shadow", Ctx()));
        Assert.IsTrue(await store.IsEnabledAsync("advisor.shadow", Ctx(project: "p1")));
    }

    [TestMethod]
    public async Task AProjectFlagOverridesTheGlobalOne()
    {
        var store = new InMemoryFeatureFlagStore();
        await store.SetAsync(new FeatureFlag("advisor.shadow", true));
        await store.SetAsync(new FeatureFlag("advisor.shadow", false, FlagScope.Project, "p1"));

        Assert.IsFalse(await store.IsEnabledAsync("advisor.shadow", Ctx(project: "p1")));
        Assert.IsTrue(await store.IsEnabledAsync("advisor.shadow", Ctx(project: "p2")));
    }

    [TestMethod]
    public async Task AFlagForAnotherScopeDoesNotApply()
    {
        var store = new InMemoryFeatureFlagStore();
        await store.SetAsync(new FeatureFlag("advisor.shadow", true, FlagScope.Company, "c1"));

        Assert.IsTrue(await store.IsEnabledAsync("advisor.shadow", Ctx(company: "c1")));
        Assert.IsFalse(await store.IsEnabledAsync("advisor.shadow", Ctx(company: "c2")));
        Assert.IsFalse(await store.IsEnabledAsync("advisor.shadow", Ctx()));
    }

    [TestMethod]
    public async Task TheMostSpecificScopeWins()
    {
        var store = new InMemoryFeatureFlagStore();
        await store.SetAsync(new FeatureFlag("advisor.shadow", false));
        await store.SetAsync(new FeatureFlag("advisor.shadow", true, FlagScope.Tenant, "t1"));
        await store.SetAsync(new FeatureFlag("advisor.shadow", false, FlagScope.Company, "c1"));
        await store.SetAsync(new FeatureFlag("advisor.shadow", true, FlagScope.Project, "p1"));
        await store.SetAsync(new FeatureFlag("advisor.shadow", false, FlagScope.User, "u1"));

        var context = Ctx(tenant: "t1", company: "c1", project: "p1", user: "u1");

        Assert.IsFalse(await store.IsEnabledAsync("advisor.shadow", context), "The user scope is the most specific.");
        Assert.IsTrue(await store.IsEnabledAsync("advisor.shadow", context with { UserId = "u2" }), "The project scope wins next.");
    }

    [TestMethod]
    public async Task SettingTheSameFlagTwiceReplacesIt()
    {
        var store = new InMemoryFeatureFlagStore();
        await store.SetAsync(new FeatureFlag("advisor.shadow", true, FlagScope.Company, "c1"));
        await store.SetAsync(new FeatureFlag("advisor.shadow", false, FlagScope.Company, "c1"));

        Assert.AreEqual(1, (await store.GetAllAsync()).Count);
        Assert.IsFalse(await store.IsEnabledAsync("advisor.shadow", Ctx(company: "c1")));
    }
}
