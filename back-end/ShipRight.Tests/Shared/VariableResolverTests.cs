using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Projects;
using ShipRight.Shared;

namespace ShipRight.Tests.Shared;

[TestClass]
public class VariableResolverTests
{
    private static ProjectConfig MakeProject(string workingDir = "/home/user/project") => new()
    {
        Id = "test-project",
        Name = "Test Project",
        Wsl = new WslConfig { WorkingDir = workingDir },
    };

    [TestMethod]
    public void Resolve_NoVariables_ReturnsInput()
    {
        var result = VariableResolver.Resolve("hello world", [], MakeProject());
        Assert.AreEqual("hello world", result);
    }

    [TestMethod]
    public void Resolve_BuiltInProjectDir_ResolvesCorrectly()
    {
        var result = VariableResolver.Resolve("{ProjectDir}", [], MakeProject());
        Assert.AreEqual("/home/user/project", result);
    }

    [TestMethod]
    public void Resolve_BuiltInProjectName_ResolvesCorrectly()
    {
        var result = VariableResolver.Resolve("{ProjectName}", [], MakeProject());
        Assert.AreEqual("Test Project", result);
    }

    [TestMethod]
    public void Resolve_BuiltInProjectId_ResolvesCorrectly()
    {
        var result = VariableResolver.Resolve("{ProjectId}", [], MakeProject());
        Assert.AreEqual("test-project", result);
    }

    [TestMethod]
    public void Resolve_CustomVariable_ResolvesCorrectly()
    {
        var customVars = new Dictionary<string, string> { ["DeployDir"] = "/var/www/app" };
        var result = VariableResolver.Resolve("{DeployDir}", customVars, MakeProject());
        Assert.AreEqual("/var/www/app", result);
    }

    [TestMethod]
    public void Resolve_CustomVariableReferencesBuiltin_ResolvesInOrder()
    {
        var customVars = new Dictionary<string, string>
        {
            ["DeployDir"] = "{ProjectDir}/deploy"
        };
        var result = VariableResolver.Resolve("{DeployDir}", customVars, MakeProject());
        Assert.AreEqual("/home/user/project/deploy", result);
    }

    [TestMethod]
    public void Resolve_MultipleVariables_AllResolves()
    {
        var customVars = new Dictionary<string, string>
        {
            ["DeployDir"] = "{ProjectDir}/deploy",
            ["BackupDir"] = "{ProjectDir}/backup"
        };
        var result = VariableResolver.Resolve("{DeployDir} and {BackupDir}", customVars, MakeProject());
        Assert.AreEqual("/home/user/project/deploy and /home/user/project/backup", result);
    }

    [TestMethod]
    public void Resolve_UnresolvedVariable_LeftAsIs()
    {
        var result = VariableResolver.Resolve("{UnknownVar}", [], MakeProject());
        Assert.AreEqual("{UnknownVar}", result);
    }

    [TestMethod]
    public void Resolve_CaseInsensitive_ResolvesCorrectly()
    {
        var customVars = new Dictionary<string, string> { ["MyVar"] = "value" };
        var result = VariableResolver.Resolve("{myvar}", customVars, MakeProject());
        Assert.AreEqual("value", result);
    }

    [TestMethod]
    public void Resolve_EmptyString_ReturnsEmpty()
    {
        var result = VariableResolver.Resolve("", [], MakeProject());
        Assert.AreEqual("", result);
    }

    [TestMethod]
    public void Resolve_Null_ReturnsNull()
    {
        var result = VariableResolver.Resolve(null!, [], MakeProject());
        Assert.IsNull(result);
    }

    [TestMethod]
    public void ResolveWorkingDirectory_NullFallsBackToProjectDir()
    {
        var result = VariableResolver.ResolveWorkingDirectory(null, [], MakeProject());
        Assert.AreEqual("/home/user/project", result);
    }

    [TestMethod]
    public void ResolveWorkingDirectory_EmptyFallsBackToProjectDir()
    {
        var result = VariableResolver.ResolveWorkingDirectory("", [], MakeProject());
        Assert.AreEqual("/home/user/project", result);
    }

    [TestMethod]
    public void ResolveWorkingDirectory_CustomDir_Resolves()
    {
        var customVars = new Dictionary<string, string> { ["DeployDir"] = "/var/www" };
        var result = VariableResolver.ResolveWorkingDirectory("{DeployDir}", customVars, MakeProject());
        Assert.AreEqual("/var/www", result);
    }

    [TestMethod]
    public void ConvertPathForContext_WslContext_ReturnsPathAsIs()
    {
        var result = VariableResolver.ConvertPathForContext("/home/user/project", isWslContext: true);
        Assert.AreEqual("/home/user/project", result);
    }

    [TestMethod]
    public void FromWslPath_MntDrive_ConvertsCorrectly()
    {
        var result = VariableResolver.FromWslPath("/mnt/d/foo/bar");
        Assert.AreEqual("D:\\foo\\bar", result);
    }

    [TestMethod]
    public void FromWslPath_NonMntPath_ReturnsWslUncPath()
    {
        var result = VariableResolver.FromWslPath("/home/user/project");
        Assert.AreEqual("\\\\wsl$\\Ubuntu\\home\\user\\project", result);
    }

    [TestMethod]
    public void IsWslCommand_BashOnWindows_ReturnsTrue()
    {
        Assert.IsTrue(VariableResolver.IsWslCommand("bash", isWindows: true));
    }

    [TestMethod]
    public void IsWslCommand_ShOnWindows_ReturnsTrue()
    {
        Assert.IsTrue(VariableResolver.IsWslCommand("sh", isWindows: true));
    }

    [TestMethod]
    public void IsWslCommand_Python3OnWindows_ReturnsTrue()
    {
        Assert.IsTrue(VariableResolver.IsWslCommand("python3", isWindows: true));
    }

    [TestMethod]
    public void IsWslCommand_PwshOnWindows_ReturnsFalse()
    {
        Assert.IsFalse(VariableResolver.IsWslCommand("pwsh", isWindows: true));
    }

    [TestMethod]
    public void IsWslCommand_BashOnLinux_ReturnsFalse()
    {
        Assert.IsFalse(VariableResolver.IsWslCommand("bash", isWindows: false));
    }

    [TestMethod]
    public void Resolve_BashInContent_DoesNotConflict()
    {
        // Bash variables use $VAR or ${VAR}, not {VAR}
        var script = "#!/bin/bash\necho $HOME\necho {ProjectDir}";
        var result = VariableResolver.Resolve(script, [], MakeProject());
        Assert.AreEqual("#!/bin/bash\necho $HOME\necho /home/user/project", result);
    }

    [TestMethod]
    public void Resolve_PathWithSpaces_ResolvesCorrectly()
    {
        var customVars = new Dictionary<string, string> { ["MyDir"] = "/path/with spaces" };
        var result = VariableResolver.Resolve("{MyDir}", customVars, MakeProject());
        Assert.AreEqual("/path/with spaces", result);
    }
}
