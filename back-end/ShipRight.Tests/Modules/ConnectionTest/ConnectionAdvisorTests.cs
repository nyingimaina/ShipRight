using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.ConnectionTest;

namespace ShipRight.Tests.Modules.ConnectionTest;

/// <summary>What a probe's answers mean for a novice: each check passes, warns (fixed automatically or harmless), or fails with a plain next step.</summary>
[TestClass]
public class ConnectionAdvisorTests
{
    private static ServerFacts Facts(params string[] kv) =>
        ServerProbe.Parse(kv);

    private static readonly string[] Good =
        ["user:ubuntu", "sudo:ok", "git:ok", "docker:installed", "dockeruse:ok", "compose:ok", "disk:50000", "dir:ok", "pkg:apt"];

    private static ServerFacts With(params string[] overrides) => Facts(Good.Concat(overrides).ToArray());

    private static CheckItem Item(IEnumerable<CheckItem> items, string id) => items.Single(i => i.Id == id);

    private static IReadOnlyList<CheckItem> Run(ServerFacts f, string? dir = "/home/ubuntu/shop") => ConnectionAdvisor.Evaluate(f, dir);

    [TestMethod]
    public void AHealthyServer_PassesEverything_InAFixedOrder()
    {
        var items = Run(With());
        CollectionAssert.AreEqual(new[] { "sudo", "docker", "compose", "git", "disk", "folder" }, items.Select(i => i.Id).ToArray());
        Assert.IsTrue(items.All(i => i.Status == CheckStatus.Pass), string.Join(", ", items.Select(i => i.Id + "=" + i.Status)));
        Assert.IsTrue(items.All(i => !string.IsNullOrWhiteSpace(i.Title) && !string.IsNullOrWhiteSpace(i.Message)));
    }

    // ── sudo ─────────────────────────────────────────────────────────────────

    [TestMethod]
    public void RootUser_CountsAsAbleToInstall() =>
        Assert.AreEqual(CheckStatus.Pass, Item(Run(With("sudo:root")), "sudo").Status);

    [TestMethod]
    public void SudoThatNeedsAPassword_IsHarmless_WhenNothingHasToBeInstalled()
    {
        var sudo = Item(Run(With("sudo:password")), "sudo");
        Assert.AreEqual(CheckStatus.Pass, sudo.Status);
        StringAssert.Contains(sudo.Message, "not needed");
    }

    [TestMethod]
    public void SudoThatNeedsAPassword_IsAFailure_WhenSomethingMustBeInstalled_AndOffersAnExplicitOptIn()
    {
        var sudo = Item(Run(With("sudo:password", "git:missing")), "sudo");
        Assert.AreEqual(CheckStatus.Fail, sudo.Status);
        StringAssert.Contains(sudo.Message, "password");
        StringAssert.Contains(sudo.Command!, "NOPASSWD");
        StringAssert.Contains(sudo.Fix!, "full administrator", "the broad grant is spelled out before the user copies it");
        StringAssert.Contains(sudo.Command!, "$USER");
    }

    [TestMethod]
    public void NoSudoAtAll_IsHandledLikeAPasswordNeed() =>
        Assert.AreEqual(CheckStatus.Fail, Item(Run(With("sudo:none", "docker:missing")), "sudo").Status);

    // ── docker ───────────────────────────────────────────────────────────────

    [TestMethod]
    public void DockerNotInstalled_WithSudo_IsAWarning_ThatTheFirstDeployFixes()
    {
        var items = Run(With("docker:missing", "git:missing"));
        var docker = Item(items, "docker");
        Assert.AreEqual(CheckStatus.Warn, docker.Status);
        StringAssert.Contains(docker.Message, "automatically");
        Assert.AreEqual(CheckStatus.Skipped, Item(items, "compose").Status, "compose is checked after docker exists");
        Assert.AreEqual(CheckStatus.Warn, Item(items, "git").Status);
        Assert.AreEqual(CheckStatus.Pass, Item(items, "sudo").Status);
    }

    [TestMethod]
    public void DockerNotInstalled_WithoutSudo_IsAFailure_WithTheRightInstallCommandForThePackageManager()
    {
        var apt = Item(Run(With("docker:missing", "sudo:password", "pkg:apt")), "docker");
        Assert.AreEqual(CheckStatus.Fail, apt.Status);
        StringAssert.Contains(apt.Command!, "apt-get install -y");
        StringAssert.Contains(apt.Command!, "docker.io");

        var dnf = Item(Run(With("docker:missing", "sudo:password", "pkg:dnf")), "docker");
        StringAssert.Contains(dnf.Command!, "dnf install -y");
    }

    [TestMethod]
    public void DockerThatThisUserMayNotUse_WithSudo_IsFixedAutomatically_ByTheDockerGroup()
    {
        var docker = Item(Run(With("dockeruse:needs-group")), "docker");
        Assert.AreEqual(CheckStatus.Warn, docker.Status);
        StringAssert.Contains(docker.Message, "automatically");
        StringAssert.Contains(docker.Command!, "usermod -aG docker \"$USER\"");
        StringAssert.Contains(docker.Fix!, "log in again", "a group change only applies to the next connection");
    }

    [TestMethod]
    public void DockerThatThisUserMayNotUse_WithoutSudo_IsAFailure_WithTheSameCommand()
    {
        var docker = Item(Run(With("dockeruse:needs-group", "sudo:password")), "docker");
        Assert.AreEqual(CheckStatus.Fail, docker.Status);
        StringAssert.Contains(docker.Command!, "usermod -aG docker");
    }

    [TestMethod]
    public void DockerInstalledButNotRunning_SaysSo_AndGivesTheStartCommand()
    {
        var docker = Item(Run(With("dockeruse:down")), "docker");
        Assert.AreEqual(CheckStatus.Warn, docker.Status);
        StringAssert.Contains(docker.Command!, "systemctl enable --now docker");
    }

    [TestMethod]
    public void TheDockerGroupIsExplainedAsPowerful_NotHidden()
    {
        var docker = Item(Run(With("dockeruse:needs-group")), "docker");
        StringAssert.Contains(docker.Fix!, "administrator");
    }

    // ── compose and git ──────────────────────────────────────────────────────

    [TestMethod]
    public void ComposeMissing_WithDockerPresent_FollowsTheSudoRule()
    {
        Assert.AreEqual(CheckStatus.Warn, Item(Run(With("compose:missing")), "compose").Status);
        Assert.AreEqual(CheckStatus.Fail, Item(Run(With("compose:missing", "sudo:password")), "compose").Status);
    }

    [TestMethod]
    public void GitMissing_FollowsTheSudoRule_AndOffersTheInstallCommand()
    {
        var warn = Item(Run(With("git:missing")), "git");
        Assert.AreEqual(CheckStatus.Warn, warn.Status);
        var fail = Item(Run(With("git:missing", "sudo:password", "pkg:dnf")), "git");
        Assert.AreEqual(CheckStatus.Fail, fail.Status);
        StringAssert.Contains(fail.Command!, "dnf install -y git");
    }

    // ── disk ─────────────────────────────────────────────────────────────────

    [TestMethod]
    public void Disk_PassesWarnsAndFailsByHowMuchRoomThereIs()
    {
        Assert.AreEqual(CheckStatus.Pass, Item(Run(With("disk:3000")), "disk").Status);
        var warn = Item(Run(With("disk:2000")), "disk");
        Assert.AreEqual(CheckStatus.Warn, warn.Status);
        StringAssert.Contains(warn.Message, "3000");
        Assert.AreEqual(CheckStatus.Fail, Item(Run(With("disk:400")), "disk").Status);
    }

    [TestMethod]
    public void Disk_ThatCouldNotBeRead_IsAWarning_NotACrash()
    {
        Assert.AreEqual(CheckStatus.Warn, Item(Run(With("disk:")), "disk").Status);
        Assert.AreEqual(CheckStatus.Warn, Item(Run(With("disk:lots")), "disk").Status);
    }

    // ── folder ───────────────────────────────────────────────────────────────

    [TestMethod]
    public void Folder_ThatExistsOrCanBeCreated_Passes()
    {
        Assert.AreEqual(CheckStatus.Pass, Item(Run(With("dir:ok")), "folder").Status);
        Assert.AreEqual(CheckStatus.Pass, Item(Run(With("dir:creatable")), "folder").Status);
    }

    [TestMethod]
    public void Folder_ThatThisUserCannotWriteTo_Fails_WithACommandToFixIt()
    {
        foreach (var v in new[] { "dir:readonly", "dir:blocked" })
        {
            var folder = Item(Run(With(v), "/srv/shop"), "folder");
            Assert.AreEqual(CheckStatus.Fail, folder.Status, v);
            StringAssert.Contains(folder.Command!, "sudo mkdir -p '/srv/shop'");
            StringAssert.Contains(folder.Command!, "sudo chown \"$USER\" '/srv/shop'");
        }
    }

    [TestMethod]
    public void Folder_NotChosenYet_IsAWarning() =>
        Assert.AreEqual(CheckStatus.Warn, Item(Run(With("dir:unset"), null), "folder").Status);

    // ── robustness ───────────────────────────────────────────────────────────

    [TestMethod]
    public void AnEmptyProbeAnswer_GivesWarningsEverywhere_NeverACrashOrAFalseAllClear()
    {
        var items = Run(Facts());
        Assert.AreEqual(6, items.Count);
        Assert.IsFalse(items.Any(i => i.Status is CheckStatus.Pass or CheckStatus.Fail), "without evidence nothing is declared fine, and nothing is declared broken");
    }

    [TestMethod]
    public void CommandsInFixes_QuoteAnyFolderName()
    {
        var folder = Item(Run(With("dir:blocked"), "/srv/it's here"), "folder");
        StringAssert.Contains(folder.Command!, "'/srv/it'\\''s here'");
    }

    [TestMethod]
    public void NothingNamesTheDeployerOrPrunes()
    {
        var all = Run(With("sudo:password", "git:missing", "docker:missing", "dir:blocked"));
        foreach (var i in all)
        {
            Assert.IsFalse((i.Command ?? "").Contains("shipright", StringComparison.OrdinalIgnoreCase), i.Id);
            Assert.IsFalse((i.Command ?? "").Contains("prune"), i.Id);
        }
    }
}
