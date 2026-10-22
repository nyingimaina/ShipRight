using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Projects;
using ShipRight.Modules.RemoteHost;
using ShipRight.Shared.SshRunner;

namespace ShipRight.Tests.Modules.RemoteHost;

/// <summary>Prepare warns (never fails) when the HTTPS domain doesn't point at the server yet.</summary>
[TestClass]
public class RemoteDomainCheckTests
{
    [TestMethod]
    [DataRow("SHIPRIGHT_DOMAIN=shipright.jaribio.biz", "shipright.jaribio.biz")]
    [DataRow("A=1\nSHIPRIGHT_DOMAIN=ship.example.com\nB=2", "ship.example.com")]
    [DataRow("SHIPRIGHT_DOMAIN=\"ship.example.com\"", "ship.example.com")]
    [DataRow("SHIPRIGHT_DOMAIN='ship.example.com'", "ship.example.com")]
    [DataRow("  export SHIPRIGHT_DOMAIN = ship.example.com  # public address", "ship.example.com")]
    [DataRow("SHIPRIGHT_DOMAIN=old.example.com\nSHIPRIGHT_DOMAIN=new.example.com", "new.example.com")]
    [DataRow("SHIPRIGHT_DOMAIN=Ship.Example.COM", "ship.example.com")]
    [DataRow("SHIPRIGHT_DOMAIN=ship.example.com\r\nX=1\r\n", "ship.example.com")]
    public void ExtractsTheDomainFromTheEnvFile(string env, string expected) =>
        Assert.AreEqual(expected, RemoteDomainCheck.ExtractDomain(env));

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("A=1")]
    [DataRow("# SHIPRIGHT_DOMAIN=commented.example.com")]
    [DataRow("SHIPRIGHT_DOMAIN=")]
    [DataRow("MY_SHIPRIGHT_DOMAIN=ship.example.com")]
    public void NoDomain_WhenNotSet(string? env) => Assert.IsNull(RemoteDomainCheck.ExtractDomain(env));

    [TestMethod]
    [DataRow("ship.example.com; rm -rf /")]
    [DataRow("$(reboot).example.com")]
    [DataRow("a b.example.com")]
    [DataRow("localhost")]               // needs a dot: a real, public name
    [DataRow("-bad.example.com")]
    public void RefusesAnythingThatIsNotAPlainHostName(string domain) =>
        Assert.IsNull(RemoteDomainCheck.ExtractDomain($"SHIPRIGHT_DOMAIN={domain}"));

    [TestMethod]
    public void Command_ComparesDnsWithTheServersPublicIp_AndNeverFails()
    {
        var cmd = RemoteDomainCheck.BuildCommand("ship.example.com");
        StringAssert.Contains(cmd, "ship.example.com");
        StringAssert.Contains(cmd, "checkip.amazonaws.com");
        StringAssert.Contains(cmd, "getent ahostsv4");
        StringAssert.Contains(cmd, "A record");
        StringAssert.EndsWith(cmd.TrimEnd(), "true");
    }

    [TestMethod]
    public void MissingDomainCommand_ExplainsWhatToSet()
    {
        var cmd = RemoteDomainCheck.BuildMissingDomainCommand();
        StringAssert.Contains(cmd, "SHIPRIGHT_DOMAIN");
        StringAssert.EndsWith(cmd.TrimEnd(), "true");
    }

    // ── wired into Prepare ───────────────────────────────────────────────────

    private sealed class FakeSsh : ISshRunner
    {
        public List<string> Commands { get; } = [];
        public Func<string, int> ExitFor { get; set; } = _ => 0;
        public Task<int> RunAsync(string host, string username, string keyPath, string command,
            Func<string, Task>? onOutput = null, Func<string, Task>? onStderr = null, CancellationToken ct = default)
        {
            Commands.Add(command);
            return Task.FromResult(ExitFor(command));
        }
    }

    private static ServerConfig Server(string env) => new()
    {
        Host = "h", Username = "u", SshKeyPath = "/k", RemoteWorkingDir = "/srv/app",
        ComposeRepoUrl = "https://github.com/x/y.git", EnvFile = env,
    };

    [TestMethod]
    public async Task Prepare_ChecksTheDomain_AfterTheEnvIsWritten()
    {
        var ssh = new FakeSsh();
        var lines = new List<string>();
        var r = await new ResilientServerBootstrapper(ssh).RunAsync(Server("SHIPRIGHT_DOMAIN=ship.example.com"),
            l => { lines.Add(l); return Task.CompletedTask; });

        Assert.IsTrue(r.Success);
        var env = ssh.Commands.FindIndex(c => c.Contains("base64 -d"));
        var domain = ssh.Commands.FindIndex(c => c.Contains("checkip.amazonaws.com"));
        Assert.IsTrue(env >= 0 && domain > env, "domain check runs after .env is written");
        Assert.IsTrue(lines.Any(l => l.Contains("Domain check")));
    }

    [TestMethod]
    public async Task Prepare_WarnsWhenTheEnvHasNoDomain()
    {
        var ssh = new FakeSsh();
        var r = await new ResilientServerBootstrapper(ssh).RunAsync(Server("A=1"), _ => Task.CompletedTask);
        Assert.IsTrue(r.Success);
        Assert.IsTrue(ssh.Commands.Any(c => c.Contains("SHIPRIGHT_DOMAIN") && c.Contains("echo")));
    }

    [TestMethod]
    public async Task Prepare_SkipsTheCheckWhenThereIsNoEnvFile()
    {
        var ssh = new FakeSsh();
        await new ResilientServerBootstrapper(ssh).RunAsync(Server(""), _ => Task.CompletedTask);
        Assert.IsFalse(ssh.Commands.Any(c => c.Contains("checkip.amazonaws.com")));
        Assert.IsFalse(ssh.Commands.Any(c => c.Contains("SHIPRIGHT_DOMAIN")));
    }
}
