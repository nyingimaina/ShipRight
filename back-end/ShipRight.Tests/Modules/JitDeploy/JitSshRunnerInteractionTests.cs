using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.JitDeploy;
using ShipRight.Modules.Projects;
using ShipRight.Modules.Setup;
using ShipRight.Shared.SshRunner;

namespace ShipRight.Tests.Modules.JitDeploy;

/// <summary>
/// The SSH runner the app really uses is <see cref="ComposeProvisioningSshRunner"/>, which reacts to any command that
/// starts with <c>cd &lt;dir&gt; &amp;&amp; export</c> - the shape the JIT compose commands also have. They must pass
/// straight through it, without ever writing a compose file onto a server.
/// </summary>
[TestClass]
public class JitSshRunnerInteractionTests
{
    private sealed class Recorder : ISshRunner
    {
        public List<string> Commands { get; } = [];
        public Task<int> RunAsync(string host, string username, string keyPath, string command,
            Func<string, Task>? onOutput = null, Func<string, Task>? onStderr = null, CancellationToken ct = default)
        { Commands.Add(command); return Task.FromResult(0); }
    }

    private sealed class Projects(params ProjectConfig[] items) : IProjectStore
    {
        public int Count => items.Length;
        public Task<List<ProjectConfig>> GetAllAsync() => Task.FromResult(items.ToList());
        public Task<ProjectConfig?> GetByIdAsync(string id) => Task.FromResult(items.FirstOrDefault(p => p.Id == id));
        public Task<ProjectConfig?> GetByNameAsync(string name) => Task.FromResult(items.FirstOrDefault(p => p.Name == name));
        public Task SaveAsync(ProjectConfig project) => Task.CompletedTask;
        public Task DeleteAsync(string id) => Task.CompletedTask;
    }

    [TestMethod]
    public async Task JitComposeCommands_PassThroughUntouched_EvenWhenAnEnvComposeProjectSharesTheServer()
    {
        // An EnvCompose project on the same host whose folder is the JIT repo folder: the worst case for confusion.
        var envCompose = new ProjectConfig
        {
            Id = "other", Name = "Other",
            Services = [new ServiceConfig { Name = "api", DockerImageName = "o/api" }],
            Server = new ServerConfig { Host = "10.0.0.5", RemoteWorkingDir = "/home/deploy/shop/repo", DeployMode = DeployMode.EnvCompose },
        };
        var inner = new Recorder();
        var runner = new ComposeProvisioningSshRunner(inner, new Projects(envCompose));

        var target = new JitTarget("10.0.0.5", "deploy", "/k", "/home/deploy/shop");
        var ctx = new ComposeContext(target, "shop");
        TagBinding[] bindings = [new("API_TAG", "shop/api")];
        var commands = new[]
        {
            JitCommands.Build(ctx, ["api"], bindings, "r1-aaaaaaa"),
            JitCommands.StopService(ctx, "api", 30, bindings, "r1-aaaaaaa"),
            JitCommands.StartService(ctx, "api", bindings, "r1-aaaaaaa"),
            JitCommands.ProbeService(ctx, "api", bindings, "r1-aaaaaaa"),
            JitCommands.ComposeConfig(ctx, bindings, "r1-aaaaaaa"),
        };

        foreach (var c in commands) await runner.RunAsync("10.0.0.5", "deploy", "/k", c);

        CollectionAssert.AreEqual(commands, inner.Commands, "exactly the commands that were sent, and nothing extra (no compose file was written)");
    }
}
