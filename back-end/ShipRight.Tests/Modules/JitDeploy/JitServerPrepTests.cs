using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.JitDeploy;

namespace ShipRight.Tests.Modules.JitDeploy;

/// <summary>
/// The pure parts of "prepare the server": what is detected, which steps are planned and in what order, and that
/// every command is safe, idempotent and names nothing but the standard tools.
/// </summary>
[TestClass]
public class JitServerPrepTests
{
    // ── detection ────────────────────────────────────────────────────────────

    [TestMethod]
    public void Detect_ChecksEachToolWithoutChangingAnything()
    {
        var c = JitServerPrep.Detect();
        StringAssert.Contains(c, "command -v git");
        StringAssert.Contains(c, "command -v docker");
        StringAssert.Contains(c, "docker info");
        StringAssert.Contains(c, "docker compose version");
        Assert.IsFalse(c.Contains("sudo"), "detecting never needs or uses sudo");
        Assert.IsFalse(c.Contains("install"), "detecting never installs");
    }

    [TestMethod]
    public void ParseMissing_ReadsTheMarkerLines_AndIgnoresEverythingElse()
    {
        var m = JitServerPrep.ParseMissing(["some noise", "missing:git", "  missing:compose  ", "missing:unknown-thing", ""]);
        CollectionAssert.AreEquivalent(new[] { ServerTool.Git, ServerTool.Compose }, m.ToArray());
    }

    [TestMethod]
    public void ParseMissing_NothingMissing_IsEmpty() =>
        Assert.AreEqual(0, JitServerPrep.ParseMissing(["hello"]).Count);

    // ── the plan ─────────────────────────────────────────────────────────────

    private static string[] PlanOf(params ServerTool[] missing) =>
        JitServerPrep.Plan(missing).Select(s => s.Tool.ToString()).ToArray();

    [TestMethod]
    public void Plan_NothingMissing_NothingToDo() => Assert.AreEqual(0, PlanOf().Length);

    [TestMethod]
    public void Plan_OnlyWhatIsMissingIsInstalled()
    {
        CollectionAssert.AreEqual(new[] { "Git" }, PlanOf(ServerTool.Git));
        CollectionAssert.AreEqual(new[] { "Daemon" }, PlanOf(ServerTool.Daemon));
        CollectionAssert.AreEqual(new[] { "Compose" }, PlanOf(ServerTool.Compose));
    }

    [TestMethod]
    public void Plan_NoDocker_AlsoStartsItAndAddsCompose_InOrder() =>
        CollectionAssert.AreEqual(new[] { "Docker", "Daemon", "Compose", "Buildx" }, PlanOf(ServerTool.Docker));

    [TestMethod]
    public void Plan_Everything_GitFirstThenDockerThenStartThenCompose() =>
        CollectionAssert.AreEqual(new[] { "Git", "Docker", "Daemon", "Compose", "Buildx" },
            PlanOf(ServerTool.Compose, ServerTool.Daemon, ServerTool.Docker, ServerTool.Git));

    [TestMethod]
    public void Plan_EveryStepHasAPlainTitle_AndItsOwnCommand()
    {
        foreach (var s in JitServerPrep.Plan(Enum.GetValues<ServerTool>()))
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(s.Title));
            Assert.IsFalse(string.IsNullOrWhiteSpace(s.Command));
        }
    }

    // ── the commands ─────────────────────────────────────────────────────────

    private static IEnumerable<string> All() =>
        JitServerPrep.Plan(Enum.GetValues<ServerTool>()).Select(s => s.Command).Append(JitServerPrep.Detect());

    [TestMethod]
    public void EveryCommand_IsIdempotent_ByCheckingFirst()
    {
        var steps = JitServerPrep.Plan(Enum.GetValues<ServerTool>()).ToDictionary(s => s.Tool, s => s.Command);
        StringAssert.Contains(steps[ServerTool.Git], "command -v git");
        StringAssert.Contains(steps[ServerTool.Docker], "command -v docker");
        StringAssert.Contains(steps[ServerTool.Daemon], "docker info");
        StringAssert.Contains(steps[ServerTool.Compose], "docker compose version");
    }

    [TestMethod]
    public void InstallSteps_ExplainThePasswordlessSudoNeed_InPlainLanguage()
    {
        foreach (var s in JitServerPrep.Plan(Enum.GetValues<ServerTool>()))
            StringAssert.Contains(s.Command, "passwordless sudo", s.Tool.ToString());
    }

    [TestMethod]
    public void InstallGit_InstallsOnlyGit_NeverDocker()
    {
        var c = JitServerPrep.Plan([ServerTool.Git]).Single().Command;
        StringAssert.Contains(c, "install -y git");
        Assert.IsFalse(c.Contains("docker"), "a missing git must never touch an existing docker install");
    }

    [TestMethod]
    public void InstallDocker_SupportsDnfAndApt_AndSaysSoWhenNeither()
    {
        var c = JitServerPrep.Plan([ServerTool.Docker]).First().Command;
        StringAssert.Contains(c, "dnf install -y docker");
        StringAssert.Contains(c, "apt-get install -y docker.io");
        StringAssert.Contains(c, "Unsupported");
    }

    [TestMethod]
    public void NoCommand_PrunesForcesOrNamesTheDeployer()
    {
        foreach (var c in All())
        {
            Assert.IsFalse(c.Contains("prune", StringComparison.OrdinalIgnoreCase), c);
            Assert.IsFalse(c.Contains("rm -rf", StringComparison.OrdinalIgnoreCase), c);
            Assert.IsFalse(c.Contains("shipright", StringComparison.OrdinalIgnoreCase), c);
        }
    }

    // ── real bash ────────────────────────────────────────────────────────────

    private static string? Bash()
    {
        foreach (var p in new[] { @"C:\Program Files\Git\bin\bash.exe", "/bin/bash", "/usr/bin/bash" })
            if (File.Exists(p)) return p;
        return null;
    }

    private static (int Exit, string Out, string Err) Run(string bash, string args, string stdin)
    {
        var psi = new ProcessStartInfo(bash, args) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        p.StandardInput.Write(stdin);
        p.StandardInput.Close();
        var o = p.StandardOutput.ReadToEnd();
        var e = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, o, e);
    }

    [TestMethod]
    public void EveryCommand_ParsesAsRealShell()
    {
        var bash = Bash();
        if (bash is null) Assert.Inconclusive("bash is not available on this machine.");
        foreach (var c in All())
        {
            var (exit, _, err) = Run(bash!, "-n", c);
            Assert.AreEqual(0, exit, err + "\n" + c);
        }
    }

    [TestMethod]
    public void Detect_InRealShell_AlwaysExitsZero_AndPrintsOnlyMarkerLines()
    {
        var bash = Bash();
        if (bash is null) Assert.Inconclusive("bash is not available on this machine.");
        var (exit, output, err) = Run(bash!, "-s", JitServerPrep.Detect());
        Assert.AreEqual(0, exit, err);
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            StringAssert.StartsWith(line, "missing:");
    }

    [TestMethod]
    public void InstallGit_WhenGitIsAlreadyThere_DoesNothingAndSucceeds()
    {
        var bash = Bash();
        if (bash is null) Assert.Inconclusive("bash is not available on this machine.");
        var (hasGit, _, _) = Run(bash!, "-s", "command -v git >/dev/null 2>&1");
        if (hasGit != 0) Assert.Inconclusive("git is not on this machine's PATH.");
        var (exit, output, err) = Run(bash!, "-s", JitServerPrep.Plan([ServerTool.Git]).Single().Command);
        Assert.AreEqual(0, exit, err);
        StringAssert.Contains(output, "already");
    }
}
