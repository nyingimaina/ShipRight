using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.JitDeploy;
using ShipRight.Modules.Platform;
using ShipRight.Modules.Projects;

namespace ShipRight.Tests.Modules.Platform;

[TestClass]
public class ShadowAdvisorTests
{
    private static ProjectConfig Project() => new()
    {
        Id = "p1",
        Name = "Shop",
        Server = new ServerConfig { Host = "staging.example.com" },
    };

    private static JitDeployConfig Jit() => new()
    {
        Enabled = true,
        ComposeServices = [new JitConfiguredService("api", "acme/shop-api", "API_TAG", "Dockerfile", ".")],
    };

    [TestMethod]
    public async Task DeterministicAdvisor_ProposesTheServicesItCanSee()
    {
        var proposal = await new DeterministicAdvisor().ProposeAsync(new AdvisorRequest(Project(), Jit()));

        Assert.IsNotNull(proposal);
        Assert.AreEqual("p1", proposal!.ProjectId);
        Assert.AreEqual("jit", proposal.MethodId);
        CollectionAssert.Contains(proposal.Services.ToArray(), "api");
        Assert.AreEqual("deterministic", proposal.Source);
    }

    [TestMethod]
    public async Task DeterministicAdvisor_SaysWhatIsMissing()
    {
        var proposal = await new DeterministicAdvisor().ProposeAsync(
            new AdvisorRequest(Project() with { Server = new ServerConfig() }, null));

        Assert.IsNotNull(proposal);
        var notes = string.Join(" ", proposal!.Notes).ToLowerInvariant();
        StringAssert.Contains(notes, "address");
        Assert.AreEqual(0, proposal.Services.Count);
    }

    [TestMethod]
    public async Task ShadowAdvisor_DoesNothingWhileTheFlagIsOff()
    {
        var flags = new InMemoryFeatureFlagStore();
        var store = new InMemoryShadowProposalStore();
        var shadow = new ShadowAdvisor(new DeterministicAdvisor(), store, flags);

        var proposal = await shadow.ProposeAsync(new AdvisorRequest(Project(), Jit()), new FlagContext(ProjectId: "p1"));

        Assert.IsNull(proposal);
        Assert.AreEqual(0, (await store.GetAllAsync()).Count);
    }

    [TestMethod]
    public async Task ShadowAdvisor_RecordsAProposalWhenTheFlagIsOn()
    {
        var flags = new InMemoryFeatureFlagStore();
        var store = new InMemoryShadowProposalStore();
        var shadow = new ShadowAdvisor(new DeterministicAdvisor(), store, flags);
        await flags.SetAsync(new FeatureFlag(ShadowAdvisor.FlagKey, true, FlagScope.Project, "p1"));

        var proposal = await shadow.ProposeAsync(new AdvisorRequest(Project(), Jit()), new FlagContext(ProjectId: "p1"));

        Assert.IsNotNull(proposal);
        Assert.AreEqual(1, (await store.GetAllAsync()).Count);
        Assert.AreEqual("p1", (await store.GetAllAsync())[0].ProjectId);
    }

    [TestMethod]
    public async Task ShadowAdvisor_NeverChangesTheProject()
    {
        var project = Project();
        var flags = new InMemoryFeatureFlagStore();
        var store = new InMemoryShadowProposalStore();
        var shadow = new ShadowAdvisor(new DeterministicAdvisor(), store, flags);
        await flags.SetAsync(new FeatureFlag(ShadowAdvisor.FlagKey, true));

        await shadow.ProposeAsync(new AdvisorRequest(project, Jit()), new FlagContext(ProjectId: "p1"));

        Assert.AreEqual("staging.example.com", project.Server.Host);
        Assert.AreEqual(0, project.Services.Count);
    }
}
