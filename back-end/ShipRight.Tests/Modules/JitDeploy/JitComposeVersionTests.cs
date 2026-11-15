using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.JitDeploy;

namespace ShipRight.Tests.Modules.JitDeploy;

/// <summary>Installing Docker Compose aims for 2.20 or newer, which the shortcut folder needs. It is best effort and never blocks a deploy.</summary>
[TestClass]
public class JitComposeVersionTests
{
    private static string ComposeStep() => JitServerPrep.Plan([ServerTool.Compose]).Single(s => s.Tool == ServerTool.Compose).Command;

    private static readonly string? Bash = new[]
    {
        "/bin/bash", "/usr/bin/bash", @"C:\Program Files\Git\bin\bash.exe", @"C:\Program Files\Git\usr\bin\bash.exe",
    }.FirstOrDefault(File.Exists);

    private static (int Exit, string Out, string Err) Run(string args, string stdin)
    {
        if (Bash is null) Assert.Inconclusive("No POSIX bash on this machine.");
        var psi = new ProcessStartInfo(Bash!, args) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi)!;
        p.StandardInput.Write(stdin); p.StandardInput.Close();
        var o = p.StandardOutput.ReadToEnd(); var e = p.StandardError.ReadToEnd();
        p.WaitForExit(15000);
        return (p.ExitCode, o, e);
    }

    [TestMethod]
    public void The_install_step_still_starts_with_its_marker_so_it_is_recognised()
    {
        StringAssert.StartsWith(ComposeStep(), ": 'prepare:compose'; ");
    }

    [TestMethod]
    public void The_install_step_still_downloads_the_latest_plugin()
    {
        StringAssert.Contains(ComposeStep(), "releases/latest/download/docker-compose-linux-");
    }

    [TestMethod]
    public void After_installing_it_checks_the_version_is_2_20_or_newer()
    {
        var c = ComposeStep();
        StringAssert.Contains(c, "docker compose version --short");
        StringAssert.Contains(c, "2.20.0");
        StringAssert.Contains(c, "sort -V");
    }

    [TestMethod]
    public void A_version_that_is_too_old_is_explained_and_never_fails_the_step()
    {
        var c = ComposeStep();
        StringAssert.Contains(c, "older than 2.20");
        StringAssert.Contains(c, "helper script");
    }

    [TestMethod]
    public void The_step_is_valid_shell()
    {
        var (exit, _, err) = Run("-n", ComposeStep());
        Assert.AreEqual(0, exit, err);
    }

    [DataTestMethod]
    [DataRow("2.29.7", true)]
    [DataRow("2.20.0", true)]
    [DataRow("2.20.3", true)]
    [DataRow("2.3.4", false)]
    [DataRow("2.19.9", false)]
    [DataRow("1.29.2", false)]
    public void The_version_test_in_the_step_gives_the_right_answer(string version, bool recentEnough)
    {
        // Run just the comparison the step uses, with a stand-in for the docker command.
        var script = "docker() { echo '" + version + "'; }\n" + JitServerPrep.ComposeVersionCheck + "\n";
        var (exit, o, e) = Run("-s", script);
        Assert.AreEqual(0, exit, e);
        if (recentEnough) StringAssert.Contains(o, "is recent enough");
        else StringAssert.Contains(o, "older than 2.20");
    }
}
