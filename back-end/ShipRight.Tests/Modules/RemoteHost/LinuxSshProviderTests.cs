using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.RemoteHost;

namespace ShipRight.Tests.Modules.RemoteHost;

[TestClass]
public class LinuxSshProviderTests
{
    [TestMethod]
    public void BuildAuthorizeCommand_AppendsPublicKeyToAuthorizedKeys()
    {
        const string pub = "ssh-ed25519 AAAA123 shipright-proj1";
        var cmd = LinuxSshProvider.BuildAuthorizeCommand(pub);
        StringAssert.Contains(cmd, "authorized_keys");
        StringAssert.Contains(cmd, pub);
    }

    [TestMethod]
    public void BuildAuthorizeCommand_CreatesSshDir()
    {
        var cmd = LinuxSshProvider.BuildAuthorizeCommand("ssh-ed25519 AAAA test");
        StringAssert.Contains(cmd, "mkdir");
        StringAssert.Contains(cmd, ".ssh");
    }

    [TestMethod]
    public void BuildAuthorizeCommand_SetsAuthorizedKeysPermissions()
    {
        var cmd = LinuxSshProvider.BuildAuthorizeCommand("ssh-ed25519 AAAA test");
        StringAssert.Contains(cmd, "chmod 600");
    }

    [TestMethod]
    public void BuildAuthorizeCommand_SetsSshDirPermissions()
    {
        var cmd = LinuxSshProvider.BuildAuthorizeCommand("ssh-ed25519 AAAA test");
        StringAssert.Contains(cmd, "chmod 700");
    }

    [TestMethod]
    public void BuildAuthorizeCommand_UsesAppendNotOverwrite()
    {
        var cmd = LinuxSshProvider.BuildAuthorizeCommand("ssh-ed25519 AAAA test");
        // >> appends; > would overwrite and lock users out
        StringAssert.Contains(cmd, ">>");
        Assert.IsFalse(cmd.Contains("= >") || cmd.Contains(" > "),
            "Command must append (>>) not overwrite (>) the authorized_keys file");
    }
}
