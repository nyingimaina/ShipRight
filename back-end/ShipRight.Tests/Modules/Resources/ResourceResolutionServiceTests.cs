using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Projects;
using ShipRight.Modules.Resources;
using ShipRight.Modules.Resources.Models;
using ShipRight.Modules.Resources.Stores;

namespace ShipRight.Tests.Modules.Resources;

[TestClass]
public class ResourceResolutionServiceTests
{
    private class StubDockerRegistryResourceStore : IDockerRegistryResourceStore
    {
        private readonly List<DockerRegistryResource> _resources = [];
        public int Count => _resources.Count;
        public Task<List<DockerRegistryResource>> GetAllAsync() => Task.FromResult(_resources.ToList());
        public Task<DockerRegistryResource?> GetByIdAsync(Guid id) =>
            Task.FromResult(_resources.FirstOrDefault(r => r.Id == id));
        public Task<DockerRegistryResource?> GetByNameAsync(string name) =>
            Task.FromResult(_resources.FirstOrDefault(r =>
                r.Name.Equals(name, StringComparison.OrdinalIgnoreCase)));
        public Task SaveAsync(DockerRegistryResource resource)
        {
            _resources.RemoveAll(r => r.Id == resource.Id);
            _resources.Add(resource);
            return Task.CompletedTask;
        }
        public Task DeleteAsync(Guid id)
        {
            _resources.RemoveAll(r => r.Id == id);
            return Task.CompletedTask;
        }
    }

    private class StubScriptResourceStore : IScriptResourceStore
    {
        private readonly List<ScriptResource> _resources = [];
        public int Count => _resources.Count;
        public Task<List<ScriptResource>> GetAllAsync() => Task.FromResult(_resources.ToList());
        public Task<List<ScriptResource>> GetGlobalAsync() =>
            Task.FromResult(_resources.Where(s => s.Scope == PipelineScope.Global).ToList());
        public Task<List<ScriptResource>> GetByProjectAsync(Guid projectId) =>
            Task.FromResult(_resources.Where(s => s.Scope == PipelineScope.Project && s.ProjectId == projectId).ToList());
        public Task<ScriptResource?> GetByIdAsync(Guid id) =>
            Task.FromResult(_resources.FirstOrDefault(r => r.Id == id));
        public Task<ScriptResource?> GetByNameAsync(string name) =>
            Task.FromResult(_resources.FirstOrDefault(r =>
                r.Name.Equals(name, StringComparison.OrdinalIgnoreCase)));
        public Task SaveAsync(ScriptResource resource)
        {
            _resources.RemoveAll(r => r.Id == resource.Id);
            _resources.Add(resource);
            return Task.CompletedTask;
        }
        public Task DeleteAsync(Guid id)
        {
            _resources.RemoveAll(r => r.Id == id);
            return Task.CompletedTask;
        }
    }

    [TestMethod]
    public async Task ResolveDockerCredentials_WhenResourceIdSet_ReturnsResourceCreds()
    {
        var resourceId = Guid.NewGuid();
        var store = new StubDockerRegistryResourceStore();
        await store.SaveAsync(new DockerRegistryResource
        {
            Id = resourceId,
            Name = "ghcr",
            Registry = "ghcr.io",
            Username = "resource-user",
            Password = "resource-pass",
        });
        var service = new ResourceResolutionService(store, new StubScriptResourceStore());

        var svc = new ServiceConfig
        {
            Name = "api",
            DockerRegistryResourceId = resourceId,
            DockerUsername = "inline-user",
            DockerPassword = "inline-pass",
        };

        var (username, password) = await service.ResolveDockerCredentialsAsync(svc, "fallback-user", "fallback-pass");

        Assert.AreEqual("resource-user", username);
        Assert.AreEqual("resource-pass", password);
    }

    [TestMethod]
    public async Task ResolveDockerCredentials_WhenResourceIdNull_ReturnsInlineFallback()
    {
        var service = new ResourceResolutionService(new StubDockerRegistryResourceStore(), new StubScriptResourceStore());
        var svc = new ServiceConfig
        {
            Name = "api",
            DockerRegistryResourceId = null,
            DockerUsername = "inline-user",
            DockerPassword = "inline-pass",
        };

        var (username, password) = await service.ResolveDockerCredentialsAsync(svc, "fallback-user", "fallback-pass");

        Assert.AreEqual("inline-user", username);
        Assert.AreEqual("inline-pass", password);
    }

    [TestMethod]
    public async Task ResolveDockerCredentials_WhenResourceNotFound_ReturnsInlineFallback()
    {
        var service = new ResourceResolutionService(new StubDockerRegistryResourceStore(), new StubScriptResourceStore());
        var svc = new ServiceConfig
        {
            Name = "api",
            DockerRegistryResourceId = Guid.NewGuid(),
            DockerUsername = "inline-user",
            DockerPassword = "inline-pass",
        };

        var (username, password) = await service.ResolveDockerCredentialsAsync(svc, "fallback-user", "fallback-pass");

        Assert.AreEqual("inline-user", username);
        Assert.AreEqual("inline-pass", password);
    }

    [TestMethod]
    public async Task ResolveDockerCredentials_WhenInlineEmpty_UsesFallback()
    {
        var service = new ResourceResolutionService(new StubDockerRegistryResourceStore(), new StubScriptResourceStore());
        var svc = new ServiceConfig
        {
            Name = "api",
            DockerRegistryResourceId = null,
            DockerUsername = "",
            DockerPassword = "",
        };

        var (username, password) = await service.ResolveDockerCredentialsAsync(svc, "fallback-user", "fallback-pass");

        Assert.AreEqual("fallback-user", username);
        Assert.AreEqual("fallback-pass", password);
    }

    [TestMethod]
    public async Task ResolveRebuildScript_WhenResourceIdSet_ReturnsScriptContent()
    {
        var resourceId = Guid.NewGuid();
        var store = new StubScriptResourceStore();
        await store.SaveAsync(new ScriptResource
        {
            Id = resourceId,
            Name = "deploy script",
            Content = "#!/bin/bash\necho 'deployed'",
        });
        var service = new ResourceResolutionService(new StubDockerRegistryResourceStore(), store);
        var server = new ServerConfig
        {
            Host = "1.2.3.4",
            RebuildScript = "rebuild.sh",
            RebuildScriptResourceId = resourceId,
        };

        var script = await service.ResolveRebuildScriptAsync(server);

        Assert.AreEqual("#!/bin/bash\necho 'deployed'", script);
    }

    [TestMethod]
    public async Task ResolveRebuildScript_WhenResourceIdNull_ReturnsInlineScript()
    {
        var service = new ResourceResolutionService(new StubDockerRegistryResourceStore(), new StubScriptResourceStore());
        var server = new ServerConfig
        {
            Host = "1.2.3.4",
            RebuildScript = "rebuild.sh",
            RebuildScriptResourceId = null,
        };

        var script = await service.ResolveRebuildScriptAsync(server);

        Assert.AreEqual("rebuild.sh", script);
    }

    [TestMethod]
    public async Task ResolveRebuildScript_WhenResourceNotFound_ReturnsInlineScript()
    {
        var service = new ResourceResolutionService(new StubDockerRegistryResourceStore(), new StubScriptResourceStore());
        var server = new ServerConfig
        {
            Host = "1.2.3.4",
            RebuildScript = "rebuild.sh",
            RebuildScriptResourceId = Guid.NewGuid(),
        };

        var script = await service.ResolveRebuildScriptAsync(server);

        Assert.AreEqual("rebuild.sh", script);
    }
}
