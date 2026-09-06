using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Resources;
using ShipRight.Shared.CommandExecution;
using ShipRight.Shared.ProcessRunner;

namespace ShipRight.Tests.Modules.Resources;

[TestClass]
public class AwsCliInstallerTests
{
    private sealed class FakeExecutor : ICommandExecutor
    {
        public List<(string Exe, string[] Args)> Calls { get; } = [];
        public Func<string, string[], ProcessResult> Handler { get; set; } =
            (_, _) => new ProcessResult(0, "", "", TimeSpan.Zero);

        public Task<ProcessResult> RunAsync(
            string executable, string[] args, string? workingDir,
            Func<string, Task>? onOutput = null, Func<string, Task>? onError = null,
            CancellationToken ct = default, IReadOnlyDictionary<string, string>? envOverride = null,
            TimeSpan? timeout = null, string? stdin = null)
        {
            Calls.Add((executable, args));
            return Task.FromResult(Handler(executable, args));
        }
    }

    private sealed class FakeLocator : IWslToolLocator
    {
        private readonly Func<bool, string?> _resolve;

        public FakeLocator(Func<bool, string?> resolve) => _resolve = resolve;

        public Task<string?> LocateAsync(string tool, bool refresh = false, CancellationToken ct = default) =>
            Task.FromResult(_resolve(refresh));
    }

    [TestMethod]
    public async Task AlreadyInstalled_ReportsSuccessWithoutRunningCommands()
    {
        var executor = new FakeExecutor();
        var installer = new AwsCliInstaller(executor, new FakeLocator(_ => "/usr/bin/aws"));

        var result = await installer.RunAsync(new AwsCliInstallRequest());

        Assert.IsTrue(result.Success);
        Assert.AreEqual(0, executor.Calls.Count);
    }

    [TestMethod]
    public async Task SudoBlocked_ReturnsManualInstallCommand()
    {
        var executor = new FakeExecutor
        {
            Handler = (exe, args) => exe == "sudo" && args is ["-n", "true"]
                ? new ProcessResult(1, "", "a password is required", TimeSpan.Zero)
                : new ProcessResult(0, "", "", TimeSpan.Zero),
        };
        var installer = new AwsCliInstaller(executor, new FakeLocator(_ => null));

        var result = await installer.RunAsync(new AwsCliInstallRequest());

        Assert.IsFalse(result.Success);
        Assert.IsTrue(result.RequiresManualInstall);
        StringAssert.Contains(result.Command!, "awscli");
    }

    [TestMethod]
    public async Task V2FlowDownloadsUnzipsInstalls_ThenFindsCli()
    {
        var installed = false;
        var executor = new FakeExecutor
        {
            Handler = (exe, _) =>
            {
                if (exe == "curl") return new ProcessResult(0, "downloaded", "", TimeSpan.Zero);
                if (exe == "unzip") return new ProcessResult(0, "", "", TimeSpan.Zero);
                if (exe == "sudo") { installed = true; return new ProcessResult(0, "", "", TimeSpan.Zero); }
                return new ProcessResult(0, "", "", TimeSpan.Zero);
            },
        };
        var installer = new AwsCliInstaller(executor,
            new FakeLocator(refresh => refresh && installed ? "/usr/local/bin/aws" : null));

        var result = await installer.RunAsync(new AwsCliInstallRequest());

        Assert.IsTrue(result.Success);
        Assert.IsTrue(executor.Calls.Any(c => c.Exe == "curl"));
        Assert.IsTrue(executor.Calls.Any(c => c.Exe == "unzip"));
        Assert.IsTrue(executor.Calls.Any(c => c.Exe == "sudo"));
    }

    [TestMethod]
    public async Task AptFallback_UsedWhenCurlMissing()
    {
        var executor = new FakeExecutor
        {
            Handler = (exe, args) =>
            {
                if (exe == "bash" && args.Any(a => a.Contains("curl")))
                    return new ProcessResult(1, "", "curl not found", TimeSpan.Zero);
                return new ProcessResult(0, "", "", TimeSpan.Zero);
            },
        };
        var installer = new AwsCliInstaller(executor,
            new FakeLocator(refresh => refresh && executor.Calls.Any(c => c.Args.Contains("apt-get")) ? "/usr/bin/aws" : null));

        var result = await installer.RunAsync(new AwsCliInstallRequest());

        Assert.IsTrue(result.Success);
        Assert.IsTrue(executor.Calls.Any(c => c.Exe == "sudo" && c.Args.Contains("apt-get") && c.Args.Contains("update")));
        Assert.IsTrue(executor.Calls.Any(c => c.Exe == "sudo" && c.Args.Contains("apt-get") && c.Args.Contains("install")));
    }

    [TestMethod]
    public async Task PipStrategy_InstallsWithoutSudo_AndFindsCli()
    {
        var executor = new FakeExecutor();
        var installer = new AwsCliInstaller(executor,
            new FakeLocator(refresh =>
                refresh && executor.Calls.Any(c => c.Exe == "pip3") ? "/home/user/.local/bin/aws" : null));

        var result = await installer.RunAsync(new AwsCliInstallRequest { Strategy = "pip" });

        Assert.IsTrue(result.Success);
        Assert.IsTrue(executor.Calls.Any(c => c.Exe == "pip3" && c.Args.Contains("--user")));
    }

    [TestMethod]
    public async Task InstallFailure_ReturnsFailureWithOutputTail()
    {
        var executor = new FakeExecutor
        {
            Handler = (exe, _) => exe == "curl"
                ? new ProcessResult(1, "partial output", "curl error", TimeSpan.Zero)
                : new ProcessResult(0, "", "", TimeSpan.Zero),
        };
        var installer = new AwsCliInstaller(executor, new FakeLocator(_ => null));

        var result = await installer.RunAsync(new AwsCliInstallRequest());

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Message!, "Downloading");
        StringAssert.Contains(result.Message!, "partial output");
    }
}