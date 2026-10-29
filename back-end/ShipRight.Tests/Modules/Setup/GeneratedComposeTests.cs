using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Projects;
using ShipRight.Modules.Setup;

namespace ShipRight.Tests.Modules.Setup;

[TestClass]
public class GeneratedComposeTests
{
    private static ServiceConfig Svc(string name, string image, int? port = null, string compose = "") => new()
    {
        Name = name, DockerImageName = image, ComposeServiceName = compose, ContainerPort = port,
    };

    private static ProjectConfig Project(params ServiceConfig[] services) => new() { Services = [.. services] };

    [TestMethod]
    public void NoServices_ProducesNothing()
    {
        Assert.IsNull(GeneratedCompose.Render(Project()));
    }

    [TestMethod]
    public void Service_UsesTheTagVariableTheDeployInjects_WithALatestFallback()
    {
        var yml = GeneratedCompose.Render(Project(Svc("Api", "shop/api", 8080, "api")))!;

        StringAssert.Contains(yml, "  api:");
        StringAssert.Contains(yml, "image: ${API_TAG:-shop/api:latest}");
        StringAssert.Contains(yml, "restart: unless-stopped");
        StringAssert.Contains(yml, "\"8080:8080\"");
    }

    [TestMethod]
    public void TagVariable_MatchesTheOrchestratorsKeyRule()
    {
        var yml = GeneratedCompose.Render(Project(Svc("Sms Gateway-2", "shop/sms", 3000)))!;

        StringAssert.Contains(yml, "${SMS_GATEWAY_2_TAG:-shop/sms:latest}");
    }

    [TestMethod]
    public void ComposeKey_FallsBackToASlugOfTheName()
    {
        var yml = GeneratedCompose.Render(Project(Svc("Sms Gateway", "shop/sms")))!;

        StringAssert.Contains(yml, "  sms-gateway:");
    }

    [TestMethod]
    public void ServiceWithoutAPort_PublishesNothing()
    {
        var yml = GeneratedCompose.Render(Project(Svc("Worker", "shop/worker")))!;

        Assert.IsFalse(yml.Contains("ports:"));
    }

    [TestMethod]
    public void TwoServicesOnTheSamePort_GetDifferentHostPorts()
    {
        var yml = GeneratedCompose.Render(Project(Svc("A", "shop/a", 8080), Svc("B", "shop/b", 8080)))!;

        StringAssert.Contains(yml, "\"8080:8080\"");
        StringAssert.Contains(yml, "\"8081:8080\"");
    }

    [TestMethod]
    public void Output_UsesLfLineEndings()
    {
        var yml = GeneratedCompose.Render(Project(Svc("Api", "shop/api", 8080)))!;

        Assert.IsFalse(yml.Contains('\r'));
        Assert.IsTrue(yml.EndsWith('\n'));
    }
}
