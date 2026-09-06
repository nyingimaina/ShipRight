using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Resources;
using ShipRight.Modules.Resources.Models;
using ShipRight.Modules.Resources.Stores;
using ShipRight.Shared.ProcessRunner;

namespace ShipRight.Tests.Modules.Resources;

[TestClass]
public class AwsProfileValidatorTests
{
    private sealed class FakeProfileStore : IAwsProfileResourceStore
    {
        private readonly Dictionary<Guid, AwsProfileResource> _items = [];
        public int Count => _items.Count;
        public void Seed(AwsProfileResource profile) => _items[profile.Id] = profile;
        public Task<List<AwsProfileResource>> GetAllAsync() => Task.FromResult(_items.Values.ToList());
        public Task<AwsProfileResource?> GetByIdAsync(Guid id) =>
            Task.FromResult(_items.TryGetValue(id, out var p) ? p : null);
        public Task<AwsProfileResource?> GetByNameAsync(string name) =>
            Task.FromResult(_items.Values.FirstOrDefault(p =>
                p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)));
        public Task SaveAsync(AwsProfileResource resource) { _items[resource.Id] = resource; return Task.CompletedTask; }
        public Task DeleteAsync(Guid id) { _items.Remove(id); return Task.CompletedTask; }
    }

    private sealed class FakeProcessRunner : IProcessRunner
    {
        public ProcessResult Result { get; set; } = new(0, "", "", TimeSpan.Zero);
        public bool ThrowWin32 { get; set; }
        public bool ThrowTimeout { get; set; }
        public IReadOnlyDictionary<string, string>? LastEnv { get; private set; }

        public Task<ProcessResult> RunAsync(
            string executable, string[] args, string? workingDir,
            Func<string, Task>? onOutput = null, Func<string, Task>? onError = null,
            CancellationToken ct = default, IReadOnlyDictionary<string, string>? envOverride = null,
            TimeSpan? timeout = null, string? stdin = null)
        {
            LastEnv = envOverride;
            Assert.AreEqual("aws", executable);
            CollectionAssert.AreEqual(new[] { "sts", "get-caller-identity", "--output", "json" }, args);
            if (ThrowWin32) throw new global::System.ComponentModel.Win32Exception();
            if (ThrowTimeout) throw new TimeoutException();
            return Task.FromResult(Result);
        }
    }

    private static AwsProfileResource NamedProfile(string profileName = "shipright-prod") => new()
    {
        Name = "prod",
        ProfileName = profileName,
        DefaultRegion = "eu-west-1",
    };

    [TestMethod]
    public async Task ExitZero_ReturnsOkWithAccountAndArn()
    {
        var runner = new FakeProcessRunner
        {
            Result = new(0,
                """{"UserId":"AIDAABCDEFGHIJKLMNOP","Account":"123456789012","Arn":"arn:aws:iam::123456789012:user/builder"}""",
                "", TimeSpan.Zero),
        };
        var store = new FakeProfileStore();
        store.Seed(NamedProfile());
        var validator = new AwsProfileValidator(runner, store);
        var id = store.GetAllAsync().Result[0].Id;

        var result = await validator.ValidateAsync(id);

        Assert.IsTrue(result.Ok);
        Assert.AreEqual("123456789012", result.AccountId);
        Assert.AreEqual("arn:aws:iam::123456789012:user/builder", result.Arn);
        Assert.IsNotNull(runner.LastEnv);
        Assert.AreEqual("shipright-prod", runner.LastEnv["AWS_PROFILE"]);
        Assert.AreEqual("eu-west-1", runner.LastEnv["AWS_DEFAULT_REGION"]);
    }

    [TestMethod]
    public async Task ExitZero_GarbageJson_ReturnsEmptyOutput()
    {
        var runner = new FakeProcessRunner { Result = new(0, "not json", "", TimeSpan.Zero) };
        var validator = new AwsProfileValidator(runner, new FakeProfileStore());

        var result = await validator.ValidateAsync(null, NamedProfile());

        Assert.IsFalse(result.Ok);
        Assert.AreEqual("empty-output", result.ErrorCode);
    }

    [TestMethod]
    public async Task Win32Exception_ReportsCliMissing()
    {
        var runner = new FakeProcessRunner { ThrowWin32 = true };
        var validator = new AwsProfileValidator(runner, null);

        var result = await validator.ValidateAsync(null, NamedProfile());

        Assert.IsFalse(result.Ok);
        Assert.AreEqual("aws-cli-missing", result.ErrorCode);
        Assert.IsNotNull(result.Hint);
    }

    [TestMethod]
    public async Task Timeout_ReportsNetwork()
    {
        var runner = new FakeProcessRunner { ThrowTimeout = true };
        var validator = new AwsProfileValidator(runner, null);

        var result = await validator.ValidateAsync(null, NamedProfile());

        Assert.IsFalse(result.Ok);
        Assert.AreEqual("network", result.ErrorCode);
    }

    [TestMethod]
    public async Task ExpiredToken_ReportsExpired()
    {
        var runner = new FakeProcessRunner { Result = new(255, "", "ExpiredToken: The security token included in the request is expired", TimeSpan.Zero) };
        var validator = new AwsProfileValidator(runner, null);

        var result = await validator.ValidateAsync(null, NamedProfile());

        Assert.IsFalse(result.Ok);
        Assert.AreEqual("expired-token", result.ErrorCode);
        Assert.IsNotNull(result.Hint);
    }

    [TestMethod]
    public async Task AccessDenied_ReportsDenied()
    {
        var runner = new FakeProcessRunner { Result = new(254, "", "AccessDenied: User is not authorized", TimeSpan.Zero) };
        var validator = new AwsProfileValidator(runner, null);

        var result = await validator.ValidateAsync(null, NamedProfile());

        Assert.IsFalse(result.Ok);
        Assert.AreEqual("denied", result.ErrorCode);
    }

    [TestMethod]
    public async Task NoCredentials_ReportsNetworkAndSuggestsConfigure()
    {
        var runner = new FakeProcessRunner { Result = new(255, "", "Unable to locate credentials. You can configure credentials by running \"aws configure\".", TimeSpan.Zero) };
        var validator = new AwsProfileValidator(runner, null);

        var result = await validator.ValidateAsync(null, NamedProfile());

        Assert.IsFalse(result.Ok);
        Assert.AreEqual("network", result.ErrorCode);
        StringAssert.Contains(result.Hint!, "aws configure");
    }

    [TestMethod]
    public async Task NamedProfileMissing_ReportsProfileNotFound()
    {
        var runner = new FakeProcessRunner { Result = new(255, "", "The config profile (shipright-prod) could not be found", TimeSpan.Zero) };
        var validator = new AwsProfileValidator(runner, null);

        var result = await validator.ValidateAsync(null, NamedProfile());

        Assert.IsFalse(result.Ok);
        Assert.AreEqual("profile-not-found", result.ErrorCode);
        StringAssert.Contains(result.Hint!, "shipright-prod");
    }

    [TestMethod]
    public async Task MissingProfileId_ReportsProfileNotFound()
    {
        var validator = new AwsProfileValidator(new FakeProcessRunner(), new FakeProfileStore());

        var result = await validator.ValidateAsync(Guid.NewGuid());

        Assert.IsFalse(result.Ok);
        Assert.AreEqual("profile-not-found", result.ErrorCode);
    }

    [TestMethod]
    public async Task NoRunner_ReportsCliMissing()
    {
        var validator = new AwsProfileValidator(null, null);

        var result = await validator.ValidateAsync(null, NamedProfile());

        Assert.IsFalse(result.Ok);
        Assert.AreEqual("aws-cli-missing", result.ErrorCode);
    }

    [TestMethod]
    public async Task InlineProfile_WithExplicitKeys_SetsAccessKeyEnv()
    {
        var runner = new FakeProcessRunner {
            Result = new(0, """{"Arn":"arn:aws:iam::111122223333:user/x"}""", "", TimeSpan.Zero),
        };
        var validator = new AwsProfileValidator(runner, null);
        var profile = new AwsProfileResource
        {
            Name = "inline",
            AccessKeyId = "AKIAINLINEKEY",
            SecretAccessKey = "inline-secret",
            DefaultRegion = "ap-south-1",
        };

        var result = await validator.ValidateAsync(null, profile);

        Assert.IsTrue(result.Ok);
        Assert.AreEqual("AKIAINLINEKEY", runner.LastEnv!["AWS_ACCESS_KEY_ID"]);
        Assert.AreEqual("inline-secret", runner.LastEnv!["AWS_SECRET_ACCESS_KEY"]);
        Assert.AreEqual("ap-south-1", runner.LastEnv!["AWS_DEFAULT_REGION"]);
    }

    [TestMethod]
    public async Task Exit127NoSuchFile_ReportsCliMissingInsteadOfNetwork()
    {
        var runner = new FakeProcessRunner { Result = new(127, "", "env: 'aws': No such file or directory", TimeSpan.Zero) };
        var validator = new AwsProfileValidator(runner, null);

        var result = await validator.ValidateAsync(null, NamedProfile());

        Assert.IsFalse(result.Ok);
        Assert.AreEqual("aws-cli-missing", result.ErrorCode);
        Assert.IsNotNull(result.Hint);
    }

    [TestMethod]
    public async Task CommandNotFound_ReportsCliMissing()
    {
        var runner = new FakeProcessRunner { Result = new(1, "", "bash: aws: command not found", TimeSpan.Zero) };
        var validator = new AwsProfileValidator(runner, null);

        var result = await validator.ValidateAsync(null, NamedProfile());

        Assert.IsFalse(result.Ok);
        Assert.AreEqual("aws-cli-missing", result.ErrorCode);
    }

    [TestMethod]
    public async Task InvalidClientTokenId_ReportsInvalidCredentials()
    {
        var runner = new FakeProcessRunner { Result = new(255, "", "An error occurred (InvalidClientTokenId) when calling the GetCallerIdentity operation", TimeSpan.Zero) };
        var validator = new AwsProfileValidator(runner, null);

        var result = await validator.ValidateAsync(null, NamedProfile());

        Assert.IsFalse(result.Ok);
        Assert.AreEqual("invalid-credentials", result.ErrorCode);
        Assert.IsNotNull(result.Hint);
    }

    [TestMethod]
    public async Task SignatureDoesNotMatch_ReportsInvalidCredentials()
    {
        var runner = new FakeProcessRunner { Result = new(255, "", "An error occurred (SignatureDoesNotMatch) when calling the GetCallerIdentity operation", TimeSpan.Zero) };
        var validator = new AwsProfileValidator(runner, null);

        var result = await validator.ValidateAsync(null, NamedProfile());

        Assert.IsFalse(result.Ok);
        Assert.AreEqual("invalid-credentials", result.ErrorCode);
    }

    [TestMethod]
    public async Task RedactedStderr_ReportsInvalidCredentialsNotNetwork()
    {
        var runner = new FakeProcessRunner { Result = new(254, "", "[REDACTED]", TimeSpan.Zero) };
        var validator = new AwsProfileValidator(runner, null);

        var result = await validator.ValidateAsync(null, NamedProfile());

        Assert.IsFalse(result.Ok);
        Assert.AreEqual("invalid-credentials", result.ErrorCode);
    }

    [TestMethod]
    public async Task NamedProfileMissingExitZero_StillReportsProfileNotFound()
    {
        var runner = new FakeProcessRunner { Result = new(255, "", "The config profile (shipright-prod) could not be found", TimeSpan.Zero) };
        var validator = new AwsProfileValidator(runner, null);

        var result = await validator.ValidateAsync(null, NamedProfile());

        Assert.IsFalse(result.Ok);
        Assert.AreEqual("profile-not-found", result.ErrorCode);
    }
}