using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Resources;
using ShipRight.Modules.Resources.Models;
using ShipRight.Shared.CommandExecution;
using ShipRight.Shared.ProcessRunner;

namespace ShipRight.Tests.Modules.Resources;

[TestClass]
public class AwsEcrAuthProviderTests
{
    private sealed class LocatingFakeRunner : IProcessRunner
    {
        public string? LastExecutable { get; private set; }
        public string[]? LastArgs { get; private set; }

        public Task<ProcessResult> RunAsync(
            string executable, string[] args, string? workingDir,
            Func<string, Task>? onOutput = null, Func<string, Task>? onError = null,
            CancellationToken ct = default, IReadOnlyDictionary<string, string>? envOverride = null,
            TimeSpan? timeout = null, string? stdin = null)
        {
            LastExecutable = executable;
            LastArgs = args;
            var stdout = args.Contains("-lc") ? "/snap/bin/aws" : "ecr-token";
            return Task.FromResult(new ProcessResult(0, stdout, "", TimeSpan.Zero));
        }
    }

    private sealed class FakeProcessRunner : IProcessRunner
    {
        public string? LastExecutable { get; private set; }
        public string[]? LastArgs { get; private set; }
        public TimeSpan? LastTimeout { get; private set; }
        public ProcessResult Result { get; set; } = new(0, "ecr-token", "", TimeSpan.Zero);

        public Task<ProcessResult> RunAsync(
            string executable, string[] args, string? workingDir,
            Func<string, Task>? onOutput = null, Func<string, Task>? onError = null,
            CancellationToken ct = default, IReadOnlyDictionary<string, string>? envOverride = null,
            TimeSpan? timeout = null, string? stdin = null)
        {
            LastExecutable = executable;
            LastArgs = args;
            LastTimeout = timeout;
            return Task.FromResult(Result);
        }
    }

    [TestMethod]
    public void Supports_ResourceWithAwsEcrAuthType_ReturnsTrue()
    {
        var provider = new AwsEcrAuthProvider(new FakeProcessRunner());
        var resource = new DockerRegistryResource { AuthType = RegistryAuthType.AwsEcr };

        Assert.IsTrue(provider.Supports("registry.example.com", resource));
    }

    [TestMethod]
    public void Supports_EcrHostNoResource_ReturnsTrue()
    {
        var provider = new AwsEcrAuthProvider(new FakeProcessRunner());

        Assert.IsTrue(provider.Supports("123.dkr.ecr.us-east-1.amazonaws.com", null));
    }

    [TestMethod]
    public void Supports_GhcrHostNoResource_ReturnsFalse()
    {
        var provider = new AwsEcrAuthProvider(new FakeProcessRunner());

        Assert.IsFalse(provider.Supports("ghcr.io", null));
    }

    [TestMethod]
    public void Supports_ResourceWithPasswordAuthType_ReturnsFalse()
    {
        var provider = new AwsEcrAuthProvider(new FakeProcessRunner());
        var resource = new DockerRegistryResource { AuthType = RegistryAuthType.Password };

        Assert.IsFalse(provider.Supports("123.dkr.ecr.us-east-1.amazonaws.com", resource));
    }

    [TestMethod]
    public async Task GetLoginCredentials_UsesResourceAwsRegion_ReturnsAwsToken()
    {
        var runner = new FakeProcessRunner();
        var provider = new AwsEcrAuthProvider(runner);
        var resource = new DockerRegistryResource
        {
            Registry = "123.dkr.ecr.us-east-1.amazonaws.com",
            AuthType = RegistryAuthType.AwsEcr,
            AwsRegion = "us-east-1",
        };

        var (username, password) = await provider.GetLoginCredentialsAsync(
            resource.Registry, resource, "fallback-user", "fallback-pass");

        Assert.AreEqual("AWS", username);
        Assert.AreEqual("ecr-token", password);
        Assert.AreEqual("aws", runner.LastExecutable);
        CollectionAssert.AreEqual(new[] { "ecr", "get-login-password", "--region", "us-east-1" }, runner.LastArgs);
    }

    [TestMethod]
    public async Task GetLoginCredentials_ParsesRegionFromHost()
    {
        var runner = new FakeProcessRunner();
        var provider = new AwsEcrAuthProvider(runner);

        await provider.GetLoginCredentialsAsync(
            "123.dkr.ecr.eu-west-2.amazonaws.com", null, "", "");

        CollectionAssert.AreEqual(new[] { "ecr", "get-login-password", "--region", "eu-west-2" }, runner.LastArgs);
    }

    [TestMethod]
    public async Task GetLoginCredentials_UsesSixtySecondTimeout()
    {
        var runner = new FakeProcessRunner();
        var provider = new AwsEcrAuthProvider(runner);

        await provider.GetLoginCredentialsAsync(
            "123.dkr.ecr.us-east-1.amazonaws.com", null, "", "");

        Assert.AreEqual(TimeSpan.FromSeconds(60), runner.LastTimeout);
    }

    [TestMethod]
    public async Task GetLoginCredentials_Failure_FallsBackToResourceCredentials()
    {
        var runner = new FakeProcessRunner { Result = new ProcessResult(255, "", "failed", TimeSpan.Zero) };
        var provider = new AwsEcrAuthProvider(runner);
        var resource = new DockerRegistryResource
        {
            AuthType = RegistryAuthType.AwsEcr,
            AwsRegion = "us-east-1",
            Username = "resource-user",
            Password = "resource-pass",
        };

        var (username, password) = await provider.GetLoginCredentialsAsync(
            "123.dkr.ecr.us-east-1.amazonaws.com", resource, "", "");

        Assert.AreEqual("resource-user", username);
        Assert.AreEqual("resource-pass", password);
    }

    [TestMethod]
    public async Task GetLoginCredentials_Failure_NoResource_Throws()
    {
        var runner = new FakeProcessRunner { Result = new ProcessResult(255, "", "failed", TimeSpan.Zero) };
        var provider = new AwsEcrAuthProvider(runner);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            provider.GetLoginCredentialsAsync("123.dkr.ecr.us-east-1.amazonaws.com", null, "", ""));
    }

    [TestMethod]
    public async Task GetLoginCredentials_WithFullExecutor_RunsAwsViaLocatedWslPath()
    {
        var runner = new LocatingFakeRunner();
        var registry = CommandResolverRegistry.CreateDefault(new WslToolLocator(runner));
        var executor = new CommandExecutor(new ExecutionTargetProvider(), registry, runner);
        var provider = new AwsEcrAuthProvider(runner, profileStore: null, executor);

        var (username, password) = await provider.GetLoginCredentialsAsync(
            "123.dkr.ecr.us-east-1.amazonaws.com", null, "", "");

        Assert.AreEqual("AWS", username);
        Assert.AreEqual("ecr-token", password);
        Assert.AreEqual("wsl", runner.LastExecutable,
            "The aws CLI must run via WSL with the located absolute path (snap bins are not on the bare WSL PATH).");
        Assert.AreEqual("/snap/bin/aws", runner.LastArgs![0]);
        CollectionAssert.AreEqual(
            new[] { "ecr", "get-login-password", "--region", "us-east-1" },
            runner.LastArgs!.Skip(1).ToArray());
    }
}
