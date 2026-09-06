using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using ShipRight.Modules.Projects;

namespace ShipRight.Tests.Modules.Resources;

[TestClass]
public class ProjectConfigResourceFieldsTests
{
    [TestMethod]
    public void ServiceConfig_SerializesWithDockerRegistryResourceId()
    {
        var id = Guid.NewGuid();
        var svc = new ServiceConfig
        {
            Name = "api",
            DockerRegistryResourceId = id,
        };

        var json = JsonConvert.SerializeObject(svc);
        var deserialized = JsonConvert.DeserializeObject<ServiceConfig>(json);

        Assert.IsNotNull(deserialized);
        Assert.AreEqual(id, deserialized.DockerRegistryResourceId);
    }

    [TestMethod]
    public void ServiceConfig_DeserializesWithoutDockerRegistryResourceId_DefaultsToNull()
    {
        var json = """{"name":"api","dockerImageName":"test"}""";
        var deserialized = JsonConvert.DeserializeObject<ServiceConfig>(json);

        Assert.IsNotNull(deserialized);
        Assert.IsNull(deserialized.DockerRegistryResourceId);
    }

    [TestMethod]
    public void ServerConfig_SerializesWithRebuildScriptResourceId()
    {
        var id = Guid.NewGuid();
        var server = new ServerConfig
        {
            Host = "1.2.3.4",
            RebuildScriptResourceId = id,
        };

        var json = JsonConvert.SerializeObject(server);
        var deserialized = JsonConvert.DeserializeObject<ServerConfig>(json);

        Assert.IsNotNull(deserialized);
        Assert.AreEqual(id, deserialized.RebuildScriptResourceId);
    }

    [TestMethod]
    public void ServerConfig_DeserializesWithoutRebuildScriptResourceId_DefaultsToNull()
    {
        var json = """{"host":"1.2.3.4"}""";
        var deserialized = JsonConvert.DeserializeObject<ServerConfig>(json);

        Assert.IsNotNull(deserialized);
        Assert.IsNull(deserialized.RebuildScriptResourceId);
    }

    [TestMethod]
    public void ExistingProjectConfig_BackwardCompatible_NullResourceIds()
    {
        var json = """
        {
            "id": "test",
            "name": "Test",
            "services": [{"name": "api", "dockerImageName": "test/api"}],
            "server": {"host": "1.2.3.4", "username": "ubuntu"}
        }
        """;
        var deserialized = JsonConvert.DeserializeObject<ProjectConfig>(json);

        Assert.IsNotNull(deserialized);
        Assert.IsNull(deserialized.Services[0].DockerRegistryResourceId);
        Assert.IsNull(deserialized.Server.RebuildScriptResourceId);
    }
}
