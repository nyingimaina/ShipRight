using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.JitDeploy;
using ShipRight.Shared.SshRunner;

namespace ShipRight.Tests.Modules.JitDeploy;

/// <summary>
/// When compose refuses a file, docker writes the reason to stderr. The deploy must keep that stream, say what it
/// was doing (commit, release, file), and give a person something to fix — instead of an empty "not valid" line.
/// </summary>
[TestClass]
public class JitDeployDiagnosticsTests
{
    private static readonly JitTarget Target = new("h", "u", "/k", "/home/deploy/shop");

    /// <summary>A server that can answer the compose check with separate stdout and stderr streams.</summary>
    private sealed class StreamRemote : ISshRunner
    {
        private readonly FakeRemote _inner = new();
        public string[] ConfigStdout = [];
        public string[] ConfigStderr = [];
        public int ConfigExit = 1;
        public IReadOnlyList<string> Commands => _inner.Commands;

        public async Task<int> RunAsync(string host, string username, string keyPath, string command,
            Func<string, Task>? onOutput = null, Func<string, Task>? onStderr = null, CancellationToken ct = default)
        {
            if (!command.Contains("config --format json", StringComparison.Ordinal))
                return await _inner.RunAsync(host, username, keyPath, command, onOutput, onStderr, ct);

            foreach (var l in ConfigStdout) if (onOutput is not null) await onOutput(l);
            foreach (var l in ConfigStderr) if (onStderr is not null) await onStderr(l);
            return ConfigExit;
        }
    }

    private static JitDeployRequest Req() => new()
    {
        Target = Target, Project = "shop", RepoUrl = "git@github.com:o/r.git", Branch = "master",
        EnvFile = "/home/deploy/shop/.deploy-state/deploy.env",
        EnvContent = "SHIPRIGHT__MODE=Cloud\n", EnvNote = "1 from this project",
        Services = [new JitService("api", "API_TAG", "shop/api"), new JitService("web", "WEB_TAG", "shop/web")],
    };

    private const string MissingVariable =
        "error while interpolating services.app.environment.SHIPRIGHT__SMTP_USERNAME: required variable SHIPRIGHT__SMTP_USERNAME is missing a value: Set SHIPRIGHT__SMTP_USERNAME";

    // ── current behaviour: stderr is dropped, so the message says nothing ──────────────────────

    [TestMethod]
    public async Task AComposeErrorOnStderr_IsShownInTheFailure_AndInTheLiveLog()
    {
        var remote = new StreamRemote { ConfigStderr = [MissingVariable] };
        var lines = new List<string>();
        var r = await new JitDeployer(remote, new FakeClock(), new JitDeployLock())
            .DeployAsync(Req(), l => { lines.Add(l); return Task.CompletedTask; });

        Assert.AreEqual(JitOutcome.FailedBeforeChange, r.Outcome, r.Message);
        StringAssert.Contains(r.Message, MissingVariable, "docker's actual error must survive the failure");
        StringAssert.Contains(r.Message, "aaaaaaa", "say which commit was checked out");
        StringAssert.Contains(r.Message, "r1-aaaaaaa", "say which release was being built");
        StringAssert.Contains(r.Message, "docker-compose.yml", "say which compose file was read");
        Assert.IsTrue(lines.Any(l => l.Contains(MissingVariable)), "the reason must appear in the streamed deploy log, not only in the final summary");
        Assert.IsFalse(remote.Commands.Any(c => c.Contains("build --progress")), "nothing is built when compose is invalid");
    }

    [TestMethod]
    public async Task AMissingVariableError_NamesTheVariableAndWhereToSetIt()
    {
        var remote = new StreamRemote { ConfigStderr = [MissingVariable] };
        var r = await new JitDeployer(remote, new FakeClock(), new JitDeployLock())
            .DeployAsync(Req(), _ => Task.CompletedTask);

        StringAssert.Contains(r.Message, "SHIPRIGHT__SMTP_USERNAME");
        StringAssert.Contains(r.Message, "build-on-server variables");
    }

    // ── guard: the two streams must not be mixed into the JSON we parse ────────────────────────

    [TestMethod]
    public async Task StderrWarnings_OnASuccessfulComposeCheck_DoNotBreakTheDeploy()
    {
        const string json = """
            { "name": "shop", "services": {
              "db": { "image": "postgres:16" },
              "api": { "image": "shop/api:r1-aaaaaaa", "build": { "context": "/r" }, "depends_on": { "db": {} } },
              "web": { "image": "shop/web:r1-aaaaaaa", "build": { "context": "/r" }, "depends_on": { "api": {} } }
            } }
            """;
        var remote = new StreamRemote
        {
            ConfigStdout = [json],
            ConfigStderr = ["time=\"2026-11-23T18:50:22Z\" level=warning msg=\"The MYSQL_PASSWORD variable is not set. Defaulting to a blank string.\""],
            ConfigExit = 0,
        };
        var r = await new JitDeployer(remote, new FakeClock(), new JitDeployLock())
            .DeployAsync(Req(), _ => Task.CompletedTask);

        Assert.AreEqual(JitOutcome.Deployed, r.Outcome, r.Message);
    }

    // ── the formatter itself ──────────────────────────────────────────────────────────────────

    [TestMethod]
    public void ComposeFailure_UsesBothStreams_AndKeepsTheLastLines()
    {
        var message = DeployDiagnostics.ComposeFailure("compose.prod.yml", "r2-bbbbbbb", FakeRemote.Sha2,
            ["some stdout"], ["warning line", "the real reason from stderr"]);

        StringAssert.Contains(message, "the real reason from stderr");
        StringAssert.Contains(message, "compose.prod.yml");
        StringAssert.Contains(message, "bbbbbbb");
        StringAssert.Contains(message, "r2-bbbbbbb");
        Assert.IsFalse(message.Contains("some stdout"), "only the tail is shown");
    }

    [TestMethod]
    public void ComposeFailure_ForAnUnknownShape_StillShowsWhatDockerSaid()
    {
        var message = DeployDiagnostics.ComposeFailure(null, "r1-aaaaaaa", FakeRemote.Sha1, [], ["some other compose error"]);

        StringAssert.Contains(message, "some other compose error");
        StringAssert.Contains(message, "docker-compose.yml");
        StringAssert.Contains(message, "not valid");
    }
}
