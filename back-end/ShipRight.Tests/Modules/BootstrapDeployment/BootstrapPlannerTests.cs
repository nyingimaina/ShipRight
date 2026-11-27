using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.BootstrapDeployment;

namespace ShipRight.Tests.Modules.BootstrapDeployment;

[TestClass]
public class BootstrapPlannerTests
{
    private static BootstrapRequest Request() => new()
    {
        ProjectName = "Shop",
        RepoUrl = "git@github.com:acme/shop.git",
        Branch = "main",
        ComposeFile = "docker-compose.yml",
        Services =
        [
            new BootstrapService { Name = "api", Image = "acme/shop-api", Dockerfile = "Dockerfile", Context = "." },
        ],
        ServerName = "staging",
        Host = "staging.example.com",
        Username = "deploy",
        RemoteWorkingDir = "/home/deploy/shop",
        Domain = "shop.example.com",
        HealthCheckUrl = "https://shop.example.com/api/health",
    };

    private static string All(BootstrapPlan plan) =>
        string.Join(" | ", plan.Problems) + " || " + string.Join(" | ", plan.Warnings);

    [TestMethod]
    public void Plan_WithCompleteInput_ProducesAConsistentServerProjectAndJitConfig()
    {
        var plan = BootstrapPlanner.Plan(Request());

        Assert.IsTrue(plan.Ok, All(plan));
        Assert.AreEqual("shop", plan.Project.Id);
        Assert.AreEqual("Shop", plan.Project.Name);
        Assert.AreEqual(plan.Server.Id, plan.Project.ServerId);
        Assert.AreEqual(plan.Server.Id, plan.Project.Server.Id);
        Assert.AreEqual(plan.Server, plan.Project.Server);
        Assert.IsTrue(plan.Jit.Enabled);
        Assert.AreEqual("git@github.com:acme/shop.git", plan.Jit.RepoUrl);
        Assert.AreEqual("main", plan.Jit.Branch);
        Assert.AreEqual("shop", plan.Jit.ComposeProject);
        Assert.AreEqual("docker-compose.yml", plan.Jit.ComposeFile);
    }

    [TestMethod]
    public void Plan_PutsServicesInTheJitConfig_SoNothingNeedsALocalCheckout()
    {
        var plan = BootstrapPlanner.Plan(Request());

        Assert.IsTrue(plan.Ok, All(plan));
        Assert.AreEqual(0, plan.Project.Services.Count, "Services live in the JIT config, not the project.");
        Assert.AreEqual(0, plan.Project.GitRepos.Count);
        var service = plan.Jit.ComposeServices.Single();
        Assert.AreEqual("api", service.Name);
        Assert.AreEqual("acme/shop-api", service.ImageRef);
        Assert.AreEqual("API_TAG", service.TagVariable);
        Assert.AreEqual("Dockerfile", service.Dockerfile);
        Assert.AreEqual(".", service.Context);
    }

    [TestMethod]
    public void Plan_DefaultsTheRemoteFolderFromTheUserAndProjectName()
    {
        var request = Request() with { RemoteWorkingDir = "", ProjectName = "Shop Two", Username = "deploy" };

        var plan = BootstrapPlanner.Plan(request);

        Assert.AreEqual("/home/deploy/shop-two", plan.Server.RemoteWorkingDir);
    }

    [TestMethod]
    public void Plan_PutsTheRootUsersFolderUnderRoot()
    {
        var plan = BootstrapPlanner.Plan(Request() with { RemoteWorkingDir = "", Username = "root" });

        Assert.AreEqual("/root/shop", plan.Server.RemoteWorkingDir);
    }

    [TestMethod]
    public void Plan_AsksForAManagedKeyWhenNoKeyPathIsGiven()
    {
        var plan = BootstrapPlanner.Plan(Request() with { SshKeyPath = "" });

        Assert.IsTrue(plan.Ok, All(plan));
        Assert.IsTrue(plan.Server.ManagedSshKey);
        Assert.IsTrue(plan.Steps.Any(s => s.Id == "generate-key"));
        Assert.IsTrue(plan.Steps.Any(s => s.Id == "authorize-key"));
    }

    [TestMethod]
    public void Plan_UsesAGivenKeyPathAndDoesNotGenerateOne()
    {
        var plan = BootstrapPlanner.Plan(Request() with { SshKeyPath = "/keys/staging.pem" });

        Assert.IsTrue(plan.Ok, All(plan));
        Assert.IsFalse(plan.Server.ManagedSshKey);
        Assert.AreEqual("/keys/staging.pem", plan.Server.SshKeyPath);
        Assert.IsFalse(plan.Steps.Any(s => s.Id == "generate-key"));
    }

    [TestMethod]
    public void Plan_RequiresOnlyTheAddressAndTheRepository()
    {
        var plan = BootstrapPlanner.Plan(Request() with { Host = "", Username = "", RepoUrl = "" });

        Assert.IsFalse(plan.Ok);
        var problems = string.Join(" ", plan.Problems).ToLowerInvariant();
        StringAssert.Contains(problems, "address");
        StringAssert.Contains(problems, "git");
        Assert.IsFalse(problems.Contains("user"), "A sign-in user is optional at bootstrap time.");
    }

    [TestMethod]
    public void Plan_RefusesAGitAddressThatCarriesASecret()
    {
        var plan = BootstrapPlanner.Plan(Request() with { RepoUrl = "https://acme:ghp_abcdefghijklmnopqrstuvwxyz012345@github.com/acme/shop.git" });

        Assert.IsFalse(plan.Ok);
        StringAssert.Contains(string.Join(" ", plan.Problems).ToLowerInvariant(), "secret");
    }

    [TestMethod]
    public void Plan_RequiresADockerfileForEveryService()
    {
        var plan = BootstrapPlanner.Plan(Request() with
        {
            Services = [new BootstrapService { Name = "api", Image = "acme/shop-api", Dockerfile = "" }],
        });

        Assert.IsFalse(plan.Ok);
        StringAssert.Contains(string.Join(" ", plan.Problems).ToLowerInvariant(), "dockerfile");
    }

    [TestMethod]
    public void Plan_RejectsDuplicateServiceNames()
    {
        var plan = BootstrapPlanner.Plan(Request() with
        {
            Services =
            [
                new BootstrapService { Name = "api", Image = "acme/shop-api", Dockerfile = "Dockerfile" },
                new BootstrapService { Name = "API", Image = "acme/shop-api", Dockerfile = "Dockerfile" },
            ],
        });

        Assert.IsFalse(plan.Ok);
        StringAssert.Contains(string.Join(" ", plan.Problems).ToLowerInvariant(), "once");
    }

    [TestMethod]
    public void Plan_RendersTheEnvironmentInAStableOrder()
    {
        var plan = BootstrapPlanner.Plan(Request() with
        {
            Env = new Dictionary<string, string> { ["B"] = "2", ["A"] = "1" },
        });

        Assert.IsTrue(plan.Ok, All(plan));
        Assert.AreEqual("A=1\nB=2", plan.Jit.EnvText);
    }

    [TestMethod]
    public void Plan_IsStableForTheSameInput()
    {
        var request = Request();
        var first = BootstrapPlanner.Plan(request);
        var second = BootstrapPlanner.Plan(request);

        Assert.AreEqual(first.Jit.EnvText, second.Jit.EnvText);
        Assert.AreEqual(first.Project.Name, second.Project.Name);
        Assert.AreEqual(first.Jit.ComposeProject, second.Jit.ComposeProject);
        Assert.AreEqual(string.Join(",", first.Jit.ComposeServices.Select(s => s.Name)),
            string.Join(",", second.Jit.ComposeServices.Select(s => s.Name)));
    }

    // ── the ShipRight-self preset ────────────────────────────────────────────

    [TestMethod]
    public void SelfPreset_FillsInEverythingShipRightNeedsFromScratch()
    {
        var plan = BootstrapPlanner.Plan(ShipRightSelfTemplate.Create(
            host: "staging.example.com",
            username: "deploy",
            remoteWorkingDir: "/home/deploy/shipright",
            domain: "staging.shipright.example.com"));

        Assert.IsTrue(plan.Ok, All(plan));
        Assert.AreEqual("ShipRight", plan.Project.Name);
        Assert.AreEqual("shipright", plan.Project.Id);
        Assert.AreEqual("shipright", plan.Jit.ComposeProject);
        Assert.AreEqual("docker-compose.yml", plan.Jit.ComposeFile);
        StringAssert.EndsWith(plan.Jit.RepoUrl, "ShipRight.git");

        var app = plan.Jit.ComposeServices.Single();
        Assert.AreEqual("app", app.Name);
        Assert.AreEqual("shipright/app", app.ImageRef);
        Assert.AreEqual("Dockerfile", app.Dockerfile);
        Assert.AreEqual("APP_TAG", app.TagVariable);
    }

    [TestMethod]
    public void SelfPreset_RendersADatabasePasswordIntoTheConnectionString()
    {
        var plan = BootstrapPlanner.Plan(ShipRightSelfTemplate.Create(
            host: "staging.example.com",
            username: "deploy",
            remoteWorkingDir: "/home/deploy/shipright",
            domain: "staging.shipright.example.com"));

        Assert.IsTrue(plan.Ok, All(plan));
        var env = plan.Jit.EnvText.Split('\n')
            .Select(line => line.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);

        Assert.IsTrue(env.ContainsKey("MYSQL_PASSWORD"));
        Assert.IsFalse(string.IsNullOrWhiteSpace(env["MYSQL_PASSWORD"]));
        StringAssert.Contains(env["SHIPRIGHT__DB_CONNECTION"], env["MYSQL_PASSWORD"]);
        Assert.AreEqual("staging.shipright.example.com", env["SHIPRIGHT_DOMAIN"]);
        Assert.AreEqual("https://staging.shipright.example.com", env["SHIPRIGHT__PUBLIC_URL"]);
        Assert.AreEqual("Cloud", env["SHIPRIGHT__MODE"]);
        Assert.AreEqual("https://staging.shipright.example.com/api/health", plan.Server.HealthCheckUrl);
    }

    [TestMethod]
    public void Plan_ForAnotherApp_NeedsOnlyTheAddressPlusItsOwnFacts()
    {
        var plan = BootstrapPlanner.Plan(new BootstrapRequest
        {
            ProjectName = "shop",
            RepoUrl = "git@github.com:acme/shop.git",
            Services = [new BootstrapService { Name = "api", Image = "acme/shop-api", Dockerfile = "Dockerfile" }],
            Host = "203.0.113.10",
        });

        Assert.IsTrue(plan.Ok, All(plan));
        Assert.AreEqual("shop", plan.Project.Name);
        Assert.AreEqual("api", plan.Jit.ComposeServices.Single().Name);
        Assert.IsFalse(plan.DeployReady);
        Assert.IsTrue(plan.DeployBlockers.Count > 0);
    }

    [TestMethod]
    public void SelfPreset_WithoutADomain_IsStillSavedAndSaysWhatIsDeferred()
    {
        var plan = BootstrapPlanner.Plan(ShipRightSelfTemplate.Create(
            host: "203.0.113.10",
            username: "",
            remoteWorkingDir: "",
            domain: ""));

        Assert.IsTrue(plan.Ok, All(plan));
        StringAssert.Contains(string.Join(" ", plan.Warnings).ToLowerInvariant(), "domain");
        Assert.AreEqual("http://localhost:5200/api/health", plan.Server.HealthCheckUrl);
    }

    [TestMethod]
    public void Plan_WithOnlyAPublicAddress_IsAcceptedAndSaysWhatIsDeferred()
    {
        var plan = BootstrapPlanner.Plan(ShipRightSelfTemplate.Create(
            host: "203.0.113.10",
            username: "",
            remoteWorkingDir: "",
            domain: ""));

        Assert.IsTrue(plan.Ok, All(plan));
        Assert.AreEqual("203.0.113.10", plan.Server.Host);
        Assert.AreEqual("", plan.Server.Username);
        Assert.AreEqual("", plan.Server.RemoteWorkingDir);
        Assert.IsFalse(plan.DeployReady);
        Assert.IsTrue(plan.DeployBlockers.Count > 0);
        StringAssert.Contains(plan.Jit.EnvText, "SHIPRIGHT__PUBLIC_URL=http://203.0.113.10:8080");
        Assert.IsTrue(plan.Steps.Any(s => s.Id == "add-ssh"));
        Assert.IsTrue(plan.Steps.Any(s => s.Id == "add-domain"));
    }

    [TestMethod]
    public void SelfPreset_KeepsValuesTheCallerSupplied()
    {
        var request = ShipRightSelfTemplate.Create(
            host: "staging.example.com",
            username: "deploy",
            remoteWorkingDir: "/home/deploy/shipright",
            domain: "staging.shipright.example.com") with
        {
            Branch = "release",
            Env = new Dictionary<string, string> { ["MYSQL_PASSWORD"] = "chosen-by-me" },
        };

        var plan = BootstrapPlanner.Plan(request);

        Assert.AreEqual("release", plan.Jit.Branch);
        StringAssert.Contains(plan.Jit.EnvText, "MYSQL_PASSWORD=chosen-by-me");
    }
}
