using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Projects;
using ShipRight.Modules.Resources.Models;
using ShipRight.Shared.ProcessRunner;

namespace ShipRight.Tests.Modules.Resources;

[TestClass]
public class ScriptExecutorTests
{
    private static ScriptResource MakeScript(
        string name = "test script",
        string content = "echo hello",
        ScriptPlatform platform = ScriptPlatform.Bash,
        ExecutionTarget target = ExecutionTarget.Local) => new()
    {
        Name = name,
        Content = content,
        Platform = platform,
        Target = target,
        Scope = PipelineScope.Global,
        CreatedAt = DateTime.UtcNow,
        ModifiedAt = DateTime.UtcNow,
    };

    [TestMethod]
    public void GetShellExtension_Bash_ReturnsSh()
    {
        var ext = ShipRight.Modules.Resources.ScriptExecutor.GetShellExtension(ScriptPlatform.Bash);
        Assert.AreEqual(".sh", ext);
    }

    [TestMethod]
    public void GetShellExtension_PowerShell_ReturnsPs1()
    {
        var ext = ShipRight.Modules.Resources.ScriptExecutor.GetShellExtension(ScriptPlatform.PowerShell);
        Assert.AreEqual(".ps1", ext);
    }

    [TestMethod]
    public void GetShellExtension_Cmd_ReturnsCmd()
    {
        var ext = ShipRight.Modules.Resources.ScriptExecutor.GetShellExtension(ScriptPlatform.Cmd);
        Assert.AreEqual(".cmd", ext);
    }

    [TestMethod]
    public void GetShellExtension_Python_ReturnsPy()
    {
        var ext = ShipRight.Modules.Resources.ScriptExecutor.GetShellExtension(ScriptPlatform.Python);
        Assert.AreEqual(".py", ext);
    }

    [TestMethod]
    public void GetShellExtension_Sh_ReturnsSh()
    {
        var ext = ShipRight.Modules.Resources.ScriptExecutor.GetShellExtension(ScriptPlatform.Sh);
        Assert.AreEqual(".sh", ext);
    }

    [TestMethod]
    public void GetShellCommand_Bash_ReturnsBash()
    {
        var cmd = ShipRight.Modules.Resources.ScriptExecutor.GetShellCommand(ScriptPlatform.Bash);
        Assert.AreEqual("bash", cmd);
    }

    [TestMethod]
    public void GetShellCommand_PowerShell_ReturnsPwsh()
    {
        var cmd = ShipRight.Modules.Resources.ScriptExecutor.GetShellCommand(ScriptPlatform.PowerShell);
        Assert.AreEqual("pwsh", cmd);
    }

    [TestMethod]
    public void GetShellCommand_Cmd_ReturnsCmdSlashC()
    {
        var cmd = ShipRight.Modules.Resources.ScriptExecutor.GetShellCommand(ScriptPlatform.Cmd);
        Assert.AreEqual("cmd", cmd);
    }

    [TestMethod]
    public void GetShellCommand_Python_ReturnsPython3()
    {
        var cmd = ShipRight.Modules.Resources.ScriptExecutor.GetShellCommand(ScriptPlatform.Python);
        Assert.AreEqual("python3", cmd);
    }

    [TestMethod]
    public void GetShellCommand_Sh_ReturnsSh()
    {
        var cmd = ShipRight.Modules.Resources.ScriptExecutor.GetShellCommand(ScriptPlatform.Sh);
        Assert.AreEqual("sh", cmd);
    }

    [TestMethod]
    public void BuildArgs_Bash_ReturnsCorrectArgs()
    {
        var args = ShipRight.Modules.Resources.ScriptExecutor.BuildArgs(ScriptPlatform.Bash, "/tmp/test.sh");
        Assert.AreEqual(1, args.Length);
        Assert.AreEqual("/tmp/test.sh", args[0]);
    }

    [TestMethod]
    public void BuildArgs_Cmd_ReturnsSlashC()
    {
        var args = ShipRight.Modules.Resources.ScriptExecutor.BuildArgs(ScriptPlatform.Cmd, "C:\\test.cmd");
        Assert.AreEqual(2, args.Length);
        Assert.AreEqual("/c", args[0]);
        Assert.AreEqual("C:\\test.cmd", args[1]);
    }

    [TestMethod]
    public void BuildArgs_Python_ReturnsScriptPath()
    {
        var args = ShipRight.Modules.Resources.ScriptExecutor.BuildArgs(ScriptPlatform.Python, "/tmp/test.py");
        Assert.AreEqual(1, args.Length);
        Assert.AreEqual("/tmp/test.py", args[0]);
    }

    [TestMethod]
    public void BuildArgs_BashWithArgs_CorrectFormat()
    {
        var args = ShipRight.Modules.Resources.ScriptExecutor.BuildArgs(ScriptPlatform.Bash, "/tmp/test.sh");
        // bash expects: bash /tmp/test.sh
        Assert.AreEqual(1, args.Length);
    }

    [TestMethod]
    public void GetTargetDirectory_Local_ReturnsTempPath()
    {
        var dir = ShipRight.Modules.Resources.ScriptExecutor.GetTargetDirectory(ExecutionTarget.Local, null);
        Assert.IsTrue(dir.Contains(Path.GetTempPath()) || dir.Contains("tmp"));
    }

    [TestMethod]
    public void GetTargetDirectory_Remote_ReturnsRemoteTmpPath()
    {
        var serverConfig = new ServerConfig
        {
            Host = "example.com",
            Username = "deploy",
            RemoteWorkingDir = "/var/app",
        };
        var dir = ShipRight.Modules.Resources.ScriptExecutor.GetTargetDirectory(ExecutionTarget.Remote, serverConfig);
        Assert.IsTrue(dir.Contains("/tmp") || dir.Contains("tmp"));
    }
}
