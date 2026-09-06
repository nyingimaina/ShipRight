using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Shared.CommandExecution;
using ShipRight.Shared.ProcessRunner;
using ShipRight.Shared.SshRunner;

namespace ShipRight.Tests.Shared.CommandExecution;

[TestClass]
public class CommandExecutionTests
{
    private sealed class FakeProcessRunner : IProcessRunner
    {
        public string? LastExecutable { get; private set; }
        public string[]? LastArgs { get; private set; }
        public IReadOnlyDictionary<string, string>? LastEnv { get; private set; }
        public ProcessResult Result { get; set; } = new(0, "", "", TimeSpan.Zero);

        public Task<ProcessResult> RunAsync(
            string executable, string[] args, string? workingDir,
            Func<string, Task>? onOutput = null, Func<string, Task>? onError = null,
            CancellationToken ct = default, IReadOnlyDictionary<string, string>? envOverride = null,
            TimeSpan? timeout = null, string? stdin = null)
        {
            LastExecutable = executable;
            LastArgs = args;
            LastEnv = envOverride;
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeWslToolLocator : IWslToolLocator
    {
        private readonly Func<string, bool, string?> _resolve;

        public FakeWslToolLocator(Func<string, bool, string?> resolve) => _resolve = resolve;

        public Task<string?> LocateAsync(string tool, bool refresh = false, CancellationToken ct = default) =>
            Task.FromResult(_resolve(tool, refresh));
    }

    private sealed class FakeSshRunner : ISshRunner
    {
        public int ExitStatus { get; set; } = 0;
        public List<string> StdoutLines { get; } = [];
        public List<string> StderrLines { get; } = [];
        public string? LastHost { get; private set; }
        public string? LastUser { get; private set; }
        public string? LastKey { get; private set; }
        public string? LastCommand { get; private set; }

        public Task<int> RunAsync(
            string host, string username, string keyPath, string command,
            Func<string, Task>? onOutput = null, Func<string, Task>? onStderr = null,
            CancellationToken ct = default)
        {
            LastHost = host; LastUser = username; LastKey = keyPath; LastCommand = command;
            foreach (var line in StdoutLines) onOutput?.Invoke(line).GetAwaiter().GetResult();
            foreach (var line in StderrLines) (onStderr ?? onOutput)?.Invoke(line).GetAwaiter().GetResult();
            return Task.FromResult(ExitStatus);
        }
    }

    private sealed class FixedTargetProvider : IExecutionTargetProvider
    {
        private readonly ExecutionTarget _target;
        public FixedTargetProvider(ExecutionTarget target) => _target = target;
        public ExecutionTarget GetTarget() => _target;
    }

    private sealed class FakeResolver : ICommandResolver
    {
        private readonly string _executable;
        public string Name => "fake";
        public FakeResolver(string executable) => _executable = executable;
        public bool Supports(ExecutionTarget target, string executable) => executable == _executable;
        public Task<ResolvedCommand> ResolveAsync(
            ExecutionTarget target, string executable, string[] args,
            IReadOnlyDictionary<string, string>? envOverride, string? workingDir) =>
            Task.FromResult(new ResolvedCommand { Executable = "handled-" + executable, Args = args });
    }

    // ── ExecutionTarget / Provider ─────────────────────────────────────────

    [TestMethod]
    public void ExecutionTargetProvider_RemoteSettings_ReturnsSshTarget()
    {
        var provider = new ExecutionTargetProvider(key => key switch
        {
            "SHIPRIGHT__BUILD__SSH_HOST" => "203.0.113.10",
            "SHIPRIGHT__BUILD__SSH_USER" => "builder",
            _ => null,
        });

        var target = provider.GetTarget();

        Assert.IsTrue(target.IsRemote);
        Assert.AreEqual("203.0.113.10", target.Host);
        Assert.AreEqual("builder", target.Username);
        Assert.AreEqual(string.Empty, target.KeyPath);
    }

    [TestMethod]
    public void ExecutionTargetProvider_DefaultsToLocal_WhenNoHostConfigured()
    {
        var provider = new ExecutionTargetProvider(_ => null);

        var target = provider.GetTarget();

        Assert.IsFalse(target.IsRemote);
    }

    // ── Resolver selection ─────────────────────────────────────────────────

    [TestMethod]
    public async Task Registry_FirstMatchingResolver_Wins()
    {
        var registry = CommandResolverRegistry.PassthroughLocal().Register(new FakeResolver("aws"));

        var cmd = await registry.ResolveAsync(ExecutionTarget.Local, "aws", ["x"], null, null);

        Assert.AreEqual("handled-aws", cmd.Executable);
    }

    [TestMethod]
    public async Task Registry_FallsBackToLocalNative_WhenNothingSupports()
    {
        var registry = CommandResolverRegistry.PassthroughLocal().Register(new FakeResolver("aws"));

        var cmd = await registry.ResolveAsync(ExecutionTarget.Local, "docker", ["info"], null, null);

        Assert.AreEqual("docker", cmd.Executable);
        CollectionAssert.AreEqual(new[] { "info" }, cmd.Args);
    }

    // ── LocalNative resolver ───────────────────────────────────────────────

    [TestMethod]
    public async Task PassthroughLocal_PreservesExeArgsEnvAndWorkdir()
    {
        var env = new Dictionary<string, string> { ["AWS_PROFILE"] = "prod" };
        var registry = CommandResolverRegistry.PassthroughLocal();

        var cmd = await registry.ResolveAsync(ExecutionTarget.Local, "aws", ["sts"], env, "C:\\repo");

        Assert.AreEqual(CommandTransport.Local, cmd.Transport);
        Assert.AreEqual("aws", cmd.Executable);
        CollectionAssert.AreEqual(new[] { "sts" }, cmd.Args);
        Assert.AreEqual("C:\\repo", cmd.WorkingDir);
        Assert.AreSame(env, cmd.EnvOverride);
    }

    // ── WSL resolver (apt / snap / pip + fallback + path mapping) ──────────

    private static IWslToolLocator Locator(string? path) =>
        new FakeWslToolLocator((tool, refresh) => path);

    [TestMethod]
    public async Task WslResolver_AptInstall_UsesAbsolutePath()
    {
        var registry = CommandResolverRegistry.CreateDefault(Locator("/usr/bin/aws"));

        var cmd = await registry.ResolveAsync(ExecutionTarget.Local, "aws", ["sts"], null, null);

        Assert.AreEqual(CommandTransport.Local, cmd.Transport);
        Assert.AreEqual("wsl", cmd.Executable);
        Assert.AreEqual("/usr/bin/aws", cmd.Args[0]);
    }

    [TestMethod]
    public async Task WslResolver_SnapInstall_UsesAbsolutePath()
    {
        var registry = CommandResolverRegistry.CreateDefault(Locator("/snap/bin/aws"));

        var cmd = await registry.ResolveAsync(ExecutionTarget.Local, "aws", ["sts"], null, null);

        Assert.AreEqual("wsl", cmd.Executable);
        Assert.AreEqual("/snap/bin/aws", cmd.Args[0]);
    }

    [TestMethod]
    public async Task WslResolver_NotLocatable_FallsBackToBareName()
    {
        var registry = CommandResolverRegistry.CreateDefault(Locator(null));

        var cmd = await registry.ResolveAsync(ExecutionTarget.Local, "aws", ["sts"], null, null);

        Assert.AreEqual("wsl", cmd.Executable);
        Assert.AreEqual("aws", cmd.Args[0]);
    }

    [TestMethod]
    public async Task WslResolver_ConvertsWindowsDrivePathsInArgs()
    {
        var registry = CommandResolverRegistry.CreateDefault(Locator("/usr/bin/docker"));

        var cmd = await registry.ResolveAsync(ExecutionTarget.Local, "docker", ["build", "D:\\ci\\Dockerfile"], null, null);

        Assert.AreEqual("/usr/bin/docker", cmd.Args[0]);
        Assert.AreEqual("build", cmd.Args[1]);
        Assert.AreEqual("/mnt/d/ci/Dockerfile", cmd.Args[2]);
    }

    // ── SSH resolver ───────────────────────────────────────────────────────

    [TestMethod]
    public async Task SshResolver_SearchResult_BuildsLoginShellCommandWithEnv()
    {
        var target = new ExecutionTarget("ssh", "203.0.113.10", "builder", "/keys/build.pem");
        var env = new Dictionary<string, string> { ["AWS_PROFILE"] = "a b", ["AWS_DEFAULT_REGION"] = "eu-west-1" };
        var registry = CommandResolverRegistry.CreateDefault(Locator(null));

        var cmd = await registry.ResolveAsync(target, "aws", ["sts", "get-caller-identity"], env, null);

        Assert.AreEqual(CommandTransport.Ssh, cmd.Transport);
        Assert.AreEqual("203.0.113.10", cmd.SshHost);
        Assert.AreEqual("builder", cmd.SshUsername);
        Assert.AreEqual("/keys/build.pem", cmd.SshKeyPath);
        StringAssert.StartsWith(cmd.SshCommand!, "bash -lc ");
        StringAssert.Contains(cmd.SshCommand!, "AWS_PROFILE='\\''a b'\\''");
        StringAssert.Contains(cmd.SshCommand!, "AWS_DEFAULT_REGION='\\''eu-west-1'\\''");
        StringAssert.Contains(cmd.SshCommand!, "'\\''aws'\\'' '\\''sts'\\'' '\\''get-caller-identity'\\''");
    }

    // ── CommandExecutor dispatch ───────────────────────────────────────────

    [TestMethod]
    public async Task Executor_LocalTarget_RoutesThroughProcessRunner()
    {
        var runner = new FakeProcessRunner { Result = new(0, "ok", "", TimeSpan.Zero) };
        var env = new Dictionary<string, string> { ["AWS_PROFILE"] = "prod" };
        var executor = new CommandExecutor(new ExecutionTargetProvider(_ => null), CommandResolverRegistry.PassthroughLocal(), runner);

        var result = await executor.RunAsync("aws", ["sts", "get-caller-identity", "--output", "json"], null,
            envOverride: env);

        Assert.IsTrue(result.Success);
        Assert.AreEqual("aws", runner.LastExecutable);
        CollectionAssert.AreEqual(new[] { "sts", "get-caller-identity", "--output", "json" }, runner.LastArgs);
        Assert.AreSame(env, runner.LastEnv);
    }

    [TestMethod]
    public async Task Executor_WslResolver_InvokesRunnerWithResolvedPath()
    {
        var runner = new FakeProcessRunner { Result = new(0, "ok", "", TimeSpan.Zero) };
        var env = new Dictionary<string, string> { ["AWS_PROFILE"] = "nyingi" };
        var executor = new CommandExecutor(
            new ExecutionTargetProvider(_ => null),
            CommandResolverRegistry.CreateDefault(Locator("/snap/bin/aws")),
            runner);

        var result = await executor.RunAsync("aws", ["sts", "get-caller-identity", "--output", "json"], null,
            envOverride: env);

        Assert.IsTrue(result.Success);
        Assert.AreEqual("wsl", runner.LastExecutable);
        Assert.AreEqual("/snap/bin/aws", runner.LastArgs![0]);
        CollectionAssert.AreEqual(new[] { "sts", "get-caller-identity", "--output", "json" }, runner.LastArgs[1..]);
        Assert.AreSame(env, runner.LastEnv);
    }

    [TestMethod]
    public async Task Executor_SshTarget_RunsRemoteAndBuffersOutput()
    {
        var ssh = new FakeSshRunner { ExitStatus = 0 };
        ssh.StdoutLines.Add("{\"Account\":\"123\"}");
        ssh.StderrLines.Add("warn");
        var target = new ExecutionTarget("ssh", "203.0.113.10", "builder", "/keys/k.pem");
        var executor = new CommandExecutor(
            new FixedTargetProvider(target),
            CommandResolverRegistry.CreateDefault(Locator(null)),
            new FakeProcessRunner(), ssh);

        var result = await executor.RunAsync("aws", ["sts"], null);

        Assert.IsTrue(result.Success);
        Assert.AreEqual("{\"Account\":\"123\"}\n", result.StdOut);
        Assert.AreEqual("warn\n", result.StdErr);
        Assert.AreEqual("builder", ssh.LastUser);
        StringAssert.StartsWith(ssh.LastCommand!, "bash -lc ");
        StringAssert.Contains(ssh.LastCommand!, "'\\''aws'\\'' '\\''sts'\\''");
    }
}