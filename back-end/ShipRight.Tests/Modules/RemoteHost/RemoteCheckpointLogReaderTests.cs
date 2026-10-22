using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Projects;
using ShipRight.Modules.RemoteHost;
using ShipRight.Shared.SshRunner;

namespace ShipRight.Tests.Modules.RemoteHost;

[TestClass]
public class RemoteCheckpointLogReaderTests
{
    private sealed class FakeSsh : ISshRunner
    {
        public string? Command;
        public int Exit;
        public string[] Lines = [];
        public Exception? Throw;

        public async Task<int> RunAsync(string host, string username, string keyPath, string command,
            Func<string, Task>? onOutput = null, Func<string, Task>? onStderr = null, CancellationToken ct = default)
        {
            Command = command;
            if (Throw is not null) throw Throw;
            foreach (var l in Lines) if (onOutput is not null) await onOutput(l);
            return Exit;
        }
    }

    private static ServerConfig Server() => new() { Id = "s1", Host = "1.2.3.4", Username = "u", SshKeyPath = "/k" };

    [TestMethod]
    public async Task Read_ReturnsFileContentJoinedByNewlines()
    {
        var ssh = new FakeSsh { Lines = ["a OK", "b FAIL exit=1"] };
        var r = await new RemoteCheckpointLogReader(ssh).ReadAsync(Server());

        Assert.IsTrue(r.Success);
        Assert.AreEqual("a OK\nb FAIL exit=1\n", r.Content);
        StringAssert.Contains(ssh.Command!, "checkpoints.log");
    }

    [TestMethod]
    public async Task Read_EmptyOrMissingFile_IsSuccessWithExplanatoryText()
    {
        var r = await new RemoteCheckpointLogReader(new FakeSsh()).ReadAsync(Server());
        Assert.IsTrue(r.Success);
        StringAssert.Contains(r.Content, "No checkpoints");
    }

    [TestMethod]
    public async Task Read_SshFailure_ReportsError()
    {
        var r = await new RemoteCheckpointLogReader(new FakeSsh { Throw = new InvalidOperationException("auth failed") })
            .ReadAsync(Server());
        Assert.IsFalse(r.Success);
        StringAssert.Contains(r.Error!, "auth failed");
    }

    [TestMethod]
    public async Task Read_NonZeroExit_ReportsError()
    {
        var r = await new RemoteCheckpointLogReader(new FakeSsh { Exit = 255, Lines = ["Permission denied"] }).ReadAsync(Server());
        Assert.IsFalse(r.Success);
        StringAssert.Contains(r.Error!, "Permission denied");
    }
}
