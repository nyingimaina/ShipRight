using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text;
using ShipRight.Modules.Projects;
using ShipRight.Modules.Setup;
using ShipRight.Shared.SshRunner;

namespace ShipRight.Tests.Modules.Setup;

[TestClass]
public class ComposeProvisioningSshRunnerTests
{
    private const string Dir = "/srv/shop";
    private const string Deploy = "cd /srv/shop && export API_TAG=shop/api:1.0.1 && docker compose pull && docker compose up -d";

    private sealed class ScriptedSsh : ISshRunner
    {
        public readonly List<string> Commands = [];
        public readonly List<string> Output = [];
        public Func<string, int> ExitFor = _ => 0;

        public Task<int> RunAsync(string host, string username, string keyPath, string command,
            Func<string, Task>? onOutput = null, Func<string, Task>? onStderr = null, CancellationToken ct = default)
        {
            Commands.Add(command);
            return Task.FromResult(ExitFor(command));
        }
    }

    private sealed class Store(params ProjectConfig[] projects) : IProjectStore
    {
        public int Count => projects.Length;
        public Task<List<ProjectConfig>> GetAllAsync() => Task.FromResult(projects.ToList());
        public Task<ProjectConfig?> GetByIdAsync(string id) => Task.FromResult(projects.FirstOrDefault(p => p.Id == id));
        public Task<ProjectConfig?> GetByNameAsync(string name) => Task.FromResult(projects.FirstOrDefault(p => p.Name == name));
        public Task SaveAsync(ProjectConfig project) => Task.CompletedTask;
        public Task DeleteAsync(string id) => Task.CompletedTask;
    }

    private static ProjectConfig ShopProject(string dir = Dir, string host = "10.0.0.5", bool withServices = true) => new()
    {
        Id = "p1",
        Services = withServices ? [new ServiceConfig { Name = "Api", DockerImageName = "shop/api", ContainerPort = 8080 }] : [],
        Server = new ServerConfig { Host = host, RemoteWorkingDir = dir, DeployMode = DeployMode.EnvCompose },
    };

    private static ComposeProvisioningSshRunner Sut(ScriptedSsh inner, params ProjectConfig[] projects) =>
        new(inner, new Store(projects));

    private static Task<int> Run(ComposeProvisioningSshRunner sut, string command, string host = "10.0.0.5",
        Func<string, Task>? output = null) =>
        sut.RunAsync(host, "ubuntu", "key", command, output);

    [TestMethod]
    public async Task CommandsThatAreNotAnEnvComposeDeploy_PassStraightThrough()
    {
        var ssh = new ScriptedSsh();

        var exit = await Run(Sut(ssh, ShopProject()), "uptime");

        Assert.AreEqual(0, exit);
        CollectionAssert.AreEqual(new[] { "uptime" }, ssh.Commands);
    }

    [TestMethod]
    public async Task DeployForAnUnknownServer_PassesStraightThrough()
    {
        var ssh = new ScriptedSsh();

        await Run(Sut(ssh, ShopProject()), Deploy, host: "other.example.com");

        CollectionAssert.AreEqual(new[] { Deploy }, ssh.Commands);
    }

    [TestMethod]
    public async Task ProjectWithoutServices_PassesStraightThrough()
    {
        var ssh = new ScriptedSsh();

        await Run(Sut(ssh, ShopProject(withServices: false)), Deploy);

        CollectionAssert.AreEqual(new[] { Deploy }, ssh.Commands);
    }

    [TestMethod]
    public async Task ExistingComposeFile_IsLeftAlone()
    {
        var ssh = new ScriptedSsh();

        await Run(Sut(ssh, ShopProject()), Deploy);

        Assert.AreEqual(2, ssh.Commands.Count);
        StringAssert.Contains(ssh.Commands[0], "docker-compose.yml");
        Assert.AreEqual(Deploy, ssh.Commands[1]);
    }

    [TestMethod]
    public async Task MissingComposeFile_IsUploadedBeforeTheDeploy()
    {
        var ssh = new ScriptedSsh { ExitFor = c => c.StartsWith("test ") ? 1 : 0 };
        var lines = new List<string>();

        var exit = await Run(Sut(ssh, ShopProject()), Deploy, output: l => { lines.Add(l); return Task.CompletedTask; });

        Assert.AreEqual(0, exit);
        Assert.AreEqual(3, ssh.Commands.Count);
        var upload = ssh.Commands[1];
        StringAssert.Contains(upload, "mkdir -p '/srv/shop'");
        StringAssert.Contains(upload, "base64 -d");
        StringAssert.Contains(upload, "/srv/shop/docker-compose.yml");
        Assert.AreEqual(Deploy, ssh.Commands[2]);

        var b64 = upload.Split('\'').First(s => s.Length > 40 && !s.Contains(' ') && !s.Contains('/'));
        var yml = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
        StringAssert.Contains(yml, "${API_TAG:-shop/api:latest}");
        Assert.IsTrue(lines.Any(l => l.Contains("docker-compose.yml")), "the user is told a file was created");
    }

    [TestMethod]
    public async Task Upload_NeverOverwritesAFileThatAppearedMeanwhile()
    {
        var ssh = new ScriptedSsh { ExitFor = c => c.StartsWith("test ") ? 1 : 0 };

        await Run(Sut(ssh, ShopProject()), Deploy);

        StringAssert.Contains(ssh.Commands[1], "[ -e '/srv/shop/docker-compose.yml' ] ||");
    }

    [TestMethod]
    public async Task FailedUpload_StopsBeforeTheDeploy_AndSaysWhy()
    {
        var ssh = new ScriptedSsh { ExitFor = c => c.StartsWith("test ") ? 1 : c.Contains("base64") ? 7 : 0 };
        var lines = new List<string>();

        var exit = await Run(Sut(ssh, ShopProject()), Deploy, output: l => { lines.Add(l); return Task.CompletedTask; });

        Assert.AreEqual(7, exit);
        Assert.IsFalse(ssh.Commands.Contains(Deploy));
        Assert.IsTrue(lines.Any(l => l.Contains("could not create")));
    }

    [TestMethod]
    public async Task ExistingFileCheck_AcceptsEveryNameDockerComposeReads()
    {
        var ssh = new ScriptedSsh();

        await Run(Sut(ssh, ShopProject()), Deploy);

        foreach (var name in new[] { "docker-compose.yml", "docker-compose.yaml", "compose.yml", "compose.yaml" })
            StringAssert.Contains(ssh.Commands[0], name);
    }
}
