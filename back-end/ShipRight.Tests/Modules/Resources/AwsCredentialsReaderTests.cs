using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Resources;
using ShipRight.Shared.ProcessRunner;

namespace ShipRight.Tests.Modules.Resources;

[TestClass]
public class AwsCredentialsReaderTests
{
    private sealed class WslFakeRunner : IProcessRunner
    {
        public string? CredentialsOutput { get; set; }
        public string? ConfigOutput { get; set; }
        public bool Fail { get; set; }
        public string[]? LastArgs { get; private set; }

        public Task<ProcessResult> RunAsync(
            string executable, string[] args, string? workingDir,
            Func<string, Task>? onOutput = null, Func<string, Task>? onError = null,
            CancellationToken ct = default, IReadOnlyDictionary<string, string>? envOverride = null,
            TimeSpan? timeout = null, string? stdin = null)
        {
            LastArgs = args;
            if (Fail)
                return Task.FromResult(new ProcessResult(1, "", "", TimeSpan.Zero));

            var script = args.Length > 3 ? args[3] : string.Empty;
            var content = script.Contains("credentials") ? CredentialsOutput : ConfigOutput;
            return Task.FromResult(new ProcessResult(0, content ?? "", "", TimeSpan.Zero));
        }
    }

    private static string HomeDir(params string[] content) => Path.Combine(Path.GetTempPath(), $"awsreader-{Guid.NewGuid():N}");

    private static void WriteAwsFile(string home, string fileName, string content)
    {
        var dir = Path.Combine(home, ".aws");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, fileName), content);
    }

    [TestMethod]
    public async Task WslFindsFile_ReturnsWslKind()
    {
        var runner = new WslFakeRunner
        {
            CredentialsOutput = "[prod]\naws_access_key_id = AKIA1\naws_secret_access_key = s1\n",
            ConfigOutput = "[profile prod]\nregion = us-east-1\n",
        };
        var reader = new AwsCredentialsReader("C:\\not-used", true, runner);

        var source = await reader.ReadAsync();

        Assert.AreEqual(AwsCredentialsSourceKind.Wsl, source.Kind);
        Assert.IsTrue(source.FileExists);
        Assert.AreEqual(1, source.Profiles.Count);
        Assert.AreEqual("us-east-1", source.Profiles[0].Region);
        Assert.AreEqual("prod", source.Profiles[0].Name);
    }

    [TestMethod]
    public async Task WslFails_FallsBackToNative()
    {
        var home = HomeDir();
        WriteAwsFile(home, "credentials", "[default]\naws_access_key_id = AKIA1\naws_secret_access_key = s1\n");
        var runner = new WslFakeRunner { Fail = true };
        var reader = new AwsCredentialsReader(home, true, runner);

        var source = await reader.ReadAsync();

        Assert.AreEqual(AwsCredentialsSourceKind.Native, source.Kind);
        Assert.IsTrue(source.FileExists);
        Assert.AreEqual(1, source.Profiles.Count);
        Assert.AreEqual("default", source.Profiles[0].Name);
    }

    [TestMethod]
    public async Task WslEmpty_FallsBackToNative()
    {
        var home = HomeDir();
        WriteAwsFile(home, "config", "[profile dev]\nregion = eu-central-1\n");
        var runner = new WslFakeRunner { CredentialsOutput = "", ConfigOutput = "" };
        var reader = new AwsCredentialsReader(home, true, runner);

        var source = await reader.ReadAsync();

        Assert.AreEqual(AwsCredentialsSourceKind.Native, source.Kind);
        Assert.IsTrue(source.FileExists);
        Assert.AreEqual("eu-central-1", source.Profiles[0].Region);
    }

    [TestMethod]
    public async Task WslAttemptedWithNothing_ReportsNotExists()
    {
        var runner = new WslFakeRunner { CredentialsOutput = "", ConfigOutput = "" };
        var reader = new AwsCredentialsReader("C:\\missing-home", true, runner);

        var source = await reader.ReadAsync();

        Assert.AreEqual(AwsCredentialsSourceKind.Native, source.Kind);
        Assert.IsFalse(source.FileExists);
        Assert.AreEqual(0, source.Profiles.Count);
    }

    [TestMethod]
    public async Task LinuxMode_ReadsNativeOnly()
    {
        var home = HomeDir();
        WriteAwsFile(home, "credentials", "[default]\naws_access_key_id = AKIA1\naws_secret_access_key = s1\n");
        var reader = new AwsCredentialsReader(home, tryWsl: false);

        var source = await reader.ReadAsync();

        Assert.AreEqual(AwsCredentialsSourceKind.Native, source.Kind);
        Assert.IsTrue(source.FileExists);
        Assert.AreEqual("default", source.Profiles[0].Name);
    }

    [TestMethod]
    public async Task MissingNativeFiles_ReturnsNoProfiles()
    {
        var reader = new AwsCredentialsReader("C:\\definitely\\missing\\home", tryWsl: false);

        var source = await reader.ReadAsync();

        Assert.AreEqual(AwsCredentialsSourceKind.Native, source.Kind);
        Assert.IsFalse(source.FileExists);
        Assert.AreEqual(0, source.Profiles.Count);
    }
}