using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.ConnectionTest;

namespace ShipRight.Tests.Modules.ConnectionTest;

/// <summary>The one command that asks a server about itself, and the reading of its answer. It never changes anything.</summary>
[TestClass]
public class ServerProbeTests
{
    [TestMethod]
    public void Command_IsReadOnly_NeverInstallsOrWrites()
    {
        var c = ServerProbe.Command("/home/deploy/shop");
        Assert.IsFalse(c.Contains("apt-get install") || c.Contains("dnf install"), "probing never installs");
        Assert.IsFalse(c.Contains("mkdir") || c.Contains("chmod") || c.Contains("usermod") || c.Contains("systemctl"), "probing never changes anything");
        Assert.IsFalse(c.Contains("sudo -n docker") && !c.Contains("info"), "sudo is only used to test, never to act");
        Assert.IsFalse(c.Contains("shipright", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void Command_RunsInAPlainShell_WhateverTheUsersLoginShellIs() =>
        StringAssert.StartsWith(ServerProbe.Command("/srv/app"), "sh -c ");

    [TestMethod]
    public void Command_NeverWaitsForAPassword() =>
        Assert.IsFalse(ServerProbe.Command("/srv/app").Contains("sudo true") , "sudo must always be asked with -n");

    [TestMethod]
    public void Command_WithNoFolder_StillWorks() =>
        StringAssert.Contains(ServerProbe.Command(null), "dir:unset");

    [TestMethod]
    public void Command_RejectsAFolderThatIsNotAnAbsolutePath()
    {
        Assert.ThrowsException<ArgumentException>(() => ServerProbe.Command("relative/path"));
        Assert.ThrowsException<ArgumentException>(() => ServerProbe.Command("/a/../b"));
        Assert.ThrowsException<ArgumentException>(() => ServerProbe.Command("/"));
    }

    [TestMethod]
    public void Parse_ReadsKeyValueLines_IgnoringNoise()
    {
        var f = ServerProbe.Parse(["hello", "git:ok", " docker : installed ", "disk:12345", "os:Ubuntu 24.04 LTS", "bad line"]);
        Assert.AreEqual("ok", f.Get("git"));
        Assert.AreEqual("installed", f.Get("docker"));
        Assert.AreEqual("12345", f.Get("disk"));
        Assert.AreEqual("Ubuntu 24.04 LTS", f.Get("os"));
        Assert.IsNull(f.Get("nothing"));
    }

    [TestMethod]
    public void Parse_TheLastValueWins_AndKeysAreCaseInsensitive()
    {
        var f = ServerProbe.Parse(["Git:missing", "git:ok"]);
        Assert.AreEqual("ok", f.Get("GIT"));
    }

    // ── real shell ───────────────────────────────────────────────────────────

    private static string? Bash()
    {
        foreach (var p in new[] { @"C:\Program Files\Git\bin\bash.exe", "/bin/bash", "/usr/bin/bash" })
            if (File.Exists(p)) return p;
        return null;
    }

    private static (int Exit, string Out, string Err) RunBash(string args, string stdin)
    {
        var psi = new ProcessStartInfo(Bash()!, args) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        p.StandardInput.Write(stdin);
        p.StandardInput.Close();
        var o = p.StandardOutput.ReadToEnd();
        var e = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, o, e);
    }

    [TestMethod]
    public void Command_ParsesAsRealShell_IncludingAnAwkwardFolderName()
    {
        if (Bash() is null) Assert.Inconclusive("bash is not available.");
        foreach (var dir in new[] { "/home/deploy/shop", "/srv/my app", "/srv/it's; touch pwned; '" })
        {
            var (exit, _, err) = RunBash("-n", ServerProbe.Command(dir));
            Assert.AreEqual(0, exit, err);
        }
    }

    [TestMethod]
    public void Command_InRealShell_PrintsOnlyKeyValueLines_AndAFolderNameCannotInjectCommands()
    {
        if (Bash() is null) Assert.Inconclusive("bash is not available.");
        var (exit, output, err) = RunBash("-s", ServerProbe.Command("/tmp/x'; echo INJECTED; '"));
        Assert.AreEqual(0, exit, err);
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.IsFalse(lines.Contains("INJECTED"), "the folder name must never run as a command");
        foreach (var l in lines) StringAssert.Matches(l, new System.Text.RegularExpressions.Regex(@"^[a-z]+:"), l);
        var f = ServerProbe.Parse(lines);
        foreach (var key in new[] { "user", "sudo", "git", "docker", "disk", "dir" })
            Assert.IsNotNull(f.Get(key), $"missing '{key}' in: {output}");
    }
}
