using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.JitDeploy;
using ShipRight.Modules.Platform;
using ShipRight.Modules.Projects;

namespace ShipRight.Tests.Modules.Platform;

[TestClass]
public class DeploymentMethodTests
{
    private sealed class FakeJit : IJitConfigStore
    {
        public JitDeployConfig? Config { get; init; }
        public Task<JitDeployConfig?> GetAsync(string projectId) => Task.FromResult(Config);
        public Task SaveAsync(string projectId, JitDeployConfig config) => Task.CompletedTask;
        public Task DeleteAsync(string projectId) => Task.CompletedTask;
    }

    private static ProjectConfig Project() => new()
    {
        Id = "shop",
        Name = "Shop",
        Server = new ServerConfig
        {
            Host = "staging.example.com",
            Username = "deploy",
            SshKeyPath = "/keys/staging.pem",
            RemoteWorkingDir = "/home/deploy/shop",
        },
        GitRepos = [new GitConfig { RepoPath = "/repo/shop" }],
    };

    private static JitDeployConfig Jit() => new()
    {
        Enabled = true,
        RepoUrl = "git@github.com:acme/shop.git",
        Branch = "main",
        ComposeFile = "docker-compose.yml",
        ComposeServices = [new JitConfiguredService("api", "acme/shop-api", "API_TAG", "Dockerfile", ".")],
    };

    [TestMethod]
    public async Task JitMethod_MatchesTheExistingMapperExactly()
    {
        var project = Project();
        var config = Jit();
        var method = new JitDeploymentMethod(new FakeJit { Config = config });

        var plan = await method.PlanAsync(project);
        var expected = JitDeployMapper.Map(project, config);

        Assert.AreEqual(expected.Ok, plan.Ready);
        CollectionAssert.AreEqual(expected.Problems.ToArray(), plan.Problems.ToArray());
        Assert.AreEqual("jit", plan.MethodId);
    }

    [TestMethod]
    public async Task JitMethod_ReportsWhatIsMissingInPlainWords()
    {
        var project = Project() with { Server = new ServerConfig() };
        var method = new JitDeploymentMethod(new FakeJit { Config = Jit() });

        var plan = await method.PlanAsync(project);

        Assert.IsFalse(plan.Ready);
        StringAssert.Contains(string.Join(" ", plan.Problems).ToLowerInvariant(), "server");
        Assert.AreEqual(0, plan.Steps.Count);
    }

    [TestMethod]
    public async Task LegacyMethod_IsReadyWhenThePipelineFactsArePresent()
    {
        var plan = await new LegacyDeploymentMethod().PlanAsync(Project());

        Assert.IsTrue(plan.Ready);
        Assert.AreEqual("legacy", plan.MethodId);
        Assert.IsTrue(plan.Steps.Count > 0);
    }

    [TestMethod]
    public async Task LegacyMethod_IsBlockedWhenTheServerIsMissing()
    {
        var plan = await new LegacyDeploymentMethod().PlanAsync(Project() with { Server = new ServerConfig() });

        Assert.IsFalse(plan.Ready);
        Assert.IsTrue(plan.Problems.Count > 0);
    }

    [TestMethod]
    public void Registry_FindsEveryRegisteredMethod()
    {
        var registry = new DeploymentMethodRegistry(
        [
            new JitDeploymentMethod(new FakeJit { Config = Jit() }),
            new LegacyDeploymentMethod(),
        ]);

        Assert.AreEqual(2, registry.All.Count);
        Assert.IsNotNull(registry.Find("JIT"));
        Assert.IsNotNull(registry.Find("legacy"));
        Assert.IsNull(registry.Find("not-a-method"));
    }
}
