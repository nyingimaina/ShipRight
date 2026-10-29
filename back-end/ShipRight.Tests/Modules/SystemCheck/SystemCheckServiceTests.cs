using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.System.SystemCheck;
using ShipRight.Shared.ProcessRunner;

namespace ShipRight.Tests.Modules.SystemCheck;

[TestClass]
public class SystemCheckServiceTests
{
    private const long Gb = 1024L * 1024 * 1024;

    private sealed class Scripted : IProcessRunner
    {
        private readonly List<(string Contains, ProcessResult Result)> _rules = [];
        public List<string> Calls { get; } = [];
        public bool ThrowOnWsl;
        private readonly List<(string Contains, Action Hook)> _hooks = [];

        public void AfterRun(string contains, Action hook) => _hooks.Add((contains, hook));

        public Scripted When(string contains, string stdout = "", int exit = 0, string stderr = "")
        {
            _rules.Insert(0, (contains, new ProcessResult(exit, stdout, stderr, TimeSpan.Zero)));
            return this;
        }

        public Task<ProcessResult> RunAsync(string executable, string[] args, string? workingDir,
            Func<string, Task>? onOutput = null, Func<string, Task>? onError = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? envOverride = null, TimeSpan? timeout = null, string? stdin = null)
        {
            var line = executable + " " + string.Join(" ", args);
            Calls.Add(line);
            if (ThrowOnWsl && executable == "wsl") throw new global::System.ComponentModel.Win32Exception("The system cannot find the file specified");
            var hit = _rules.Where(r => line.Contains(r.Contains)).Select(r => (ProcessResult?)r.Result).FirstOrDefault();
            foreach (var (contains, hook) in _hooks) if (line.Contains(contains)) hook();
            if (hit is not null) return Task.FromResult(hit);
            return Task.FromResult(new ProcessResult(0, "", "", TimeSpan.Zero));
        }
    }

    /// <summary>A machine where everything is fine; tests then break one thing.</summary>
    private static Scripted Healthy() => new Scripted()
        .When("-l -q", "Ubuntu\r\n")
        .When("-l -v", "  NAME      STATE           VERSION\r\n* Ubuntu    Running         2\r\n")
        .When("command -v docker", "/usr/bin/docker\n")
        .When("docker info", "27.0.1\n")
        .When("compose version", "Docker Compose version v2.29.1\n")
        .When("buildx version", "github.com/docker/buildx v0.16.0\n")
        .When("git --version", "git version 2.43.0\n")
        .When("whoami", "nyingi\n");

    private static SystemCheckService Sut(Scripted r, long freeBytes = 100 * Gb) => new(r, () => freeBytes);

    private static SystemCheckItem Item(SystemCheckReport r, string id) => r.Items.Single(i => i.Id == id);

    // ── detection ─────────────────────────────────────────────────────

    [TestMethod]
    public async Task EverythingInPlace_IsHealthy()
    {
        var report = await Sut(Healthy()).CheckAsync(default);

        Assert.IsTrue(report.Healthy);
        Assert.IsTrue(report.Items.All(i => i.Status == CheckStatus.Ok), string.Join(", ", report.Items.Select(i => $"{i.Id}:{i.Status}")));
    }

    [TestMethod]
    public async Task WslMissing_IsAProblemWithPlainSteps_AndEverythingAfterItIsSkipped()
    {
        var r = Healthy();
        r.ThrowOnWsl = true;

        var report = await Sut(r).CheckAsync(default);

        Assert.AreEqual(CheckStatus.Problem, Item(report, "wsl").Status);
        StringAssert.Contains(Item(report, "wsl").ManualSteps, "wsl --install");
        Assert.IsNull(Item(report, "wsl").FixId, "installing WSL needs administrator rights and a restart, so it is guided, not automatic");
        foreach (var id in new[] { "distro", "docker", "daemon", "compose", "buildx", "git" })
            Assert.AreEqual(CheckStatus.Skipped, Item(report, id).Status, id);
        Assert.IsFalse(report.Healthy);
    }

    [TestMethod]
    public async Task WslCommandFailing_CountsAsMissing()
    {
        var report = await Sut(Healthy().When("--status", "", 1)).CheckAsync(default);

        Assert.AreEqual(CheckStatus.Problem, Item(report, "wsl").Status);
    }

    [TestMethod]
    public async Task NoLinuxDistro_IsAProblemWithSteps_AndDockerDesktopsInternalDistrosDoNotCount()
    {
        var r = new Scripted().When("-l -q", "docker-desktop\r\ndocker-desktop-data\r\n");

        var report = await Sut(r).CheckAsync(default);

        Assert.AreEqual(CheckStatus.Problem, Item(report, "distro").Status);
        StringAssert.Contains(Item(report, "distro").ManualSteps, "wsl --install -d Ubuntu");
        Assert.AreEqual(CheckStatus.Skipped, Item(report, "docker").Status);
    }

    [TestMethod]
    public async Task DistroListFromWsl_WhichIsUtf16_IsReadCorrectly()
    {
        var r = Healthy().When("-l -q", "U\0b\0u\0n\0t\0u\0\r\0\n\0");

        var report = await Sut(r).CheckAsync(default);

        StringAssert.Contains(Item(report, "distro").Detail, "Ubuntu");
        Assert.IsFalse(Item(report, "distro").Detail.Contains('\0'));
    }

    [TestMethod]
    public async Task WslVersion1_IsAWarning_WithTheConvertCommand()
    {
        var r = Healthy().When("-l -v", "  NAME    STATE    VERSION\r\n* Ubuntu  Running  1\r\n");

        var report = await Sut(r).CheckAsync(default);

        Assert.AreEqual(CheckStatus.Warning, Item(report, "wsl2").Status);
        StringAssert.Contains(Item(report, "wsl2").ManualSteps, "wsl --set-version Ubuntu 2");
    }

    [TestMethod]
    public async Task DockerNotInstalled_OffersTheInstallFix_AndSkipsTheChecksThatNeedIt()
    {
        var r = Healthy().When("command -v docker", "", 1);

        var report = await Sut(r).CheckAsync(default);

        Assert.AreEqual(CheckStatus.Problem, Item(report, "docker").Status);
        Assert.AreEqual("install-docker", Item(report, "docker").FixId);
        foreach (var id in new[] { "daemon", "compose", "buildx" })
            Assert.AreEqual(CheckStatus.Skipped, Item(report, id).Status, id);
        Assert.AreEqual(CheckStatus.Ok, Item(report, "git").Status, "git is independent of docker");
    }

    [TestMethod]
    public async Task DockerInstalledButNotRunning_OffersToStartIt()
    {
        var r = Healthy().When("docker info", "", 1, "Cannot connect to the Docker daemon at unix:///var/run/docker.sock. Is the docker daemon running?");

        var report = await Sut(r).CheckAsync(default);

        Assert.AreEqual(CheckStatus.Problem, Item(report, "daemon").Status);
        Assert.AreEqual("start-docker", Item(report, "daemon").FixId);
    }

    [TestMethod]
    public async Task DockerDeniedToTheUser_OffersToAddThemToTheDockerGroup()
    {
        var r = Healthy().When("docker info", "", 1, "permission denied while trying to connect to the Docker daemon socket");

        var report = await Sut(r).CheckAsync(default);

        Assert.AreEqual("add-docker-group", Item(report, "daemon").FixId);
    }

    [TestMethod]
    public async Task MissingComposeOrBuildx_ShareOneFix()
    {
        var r = Healthy().When("compose version", "", 1).When("buildx version", "", 1);

        var report = await Sut(r).CheckAsync(default);

        Assert.AreEqual("install-docker-plugins", Item(report, "compose").FixId);
        Assert.AreEqual("install-docker-plugins", Item(report, "buildx").FixId);
        Assert.AreEqual(CheckStatus.Problem, Item(report, "compose").Status);
    }

    [TestMethod]
    public async Task MissingGit_OffersToInstallIt()
    {
        var r = Healthy().When("git --version", "", 1);

        var report = await Sut(r).CheckAsync(default);

        Assert.AreEqual("install-git", Item(report, "git").FixId);
    }

    [TestMethod]
    [DataRow(100, CheckStatus.Ok)]
    [DataRow(8, CheckStatus.Warning)]
    [DataRow(2, CheckStatus.Problem)]
    public async Task LowDiskSpace_IsFlagged_WithAPointerToTheUtilities(int gb, CheckStatus expected)
    {
        var report = await Sut(Healthy(), gb * Gb).CheckAsync(default);

        var disk = Item(report, "disk");
        Assert.AreEqual(expected, disk.Status);
        if (expected != CheckStatus.Ok) StringAssert.Contains(disk.ManualSteps, "Utilities");
    }

    [TestMethod]
    public async Task UnknownFreeSpace_IsNotAProblem()
    {
        var report = await new SystemCheckService(Healthy(), () => null).CheckAsync(default);

        Assert.AreEqual(CheckStatus.Skipped, Item(report, "disk").Status);
    }

    // ── fixes ─────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Fix_StartDocker_RunsAsRootInTheChosenDistro_AndReportsTheRefreshedState()
    {
        var r = Healthy().When("docker info", "", 1, "Cannot connect to the Docker daemon");
        // After the start command the daemon answers.
        var sut = Sut(r);
        r.AfterRun("service docker start", () => r.When("docker info", "27.0.1\n"));

        var result = await sut.FixAsync("start-docker", default);

        Assert.IsTrue(result.Succeeded, result.Message);
        Assert.IsTrue(r.Calls.Any(c => c.Contains("-d Ubuntu") && c.Contains("-u root") && c.Contains("service docker start")));
        Assert.AreEqual(CheckStatus.Ok, Item(result.Report, "daemon").Status);
    }

    [TestMethod]
    public async Task Fix_AddDockerGroup_UsesTheRealUserName()
    {
        var r = Healthy().When("docker info", "", 1, "permission denied");

        await Sut(r).FixAsync("add-docker-group", default);

        Assert.IsTrue(r.Calls.Any(c => c.Contains("-u root") && c.Contains("usermod -aG docker nyingi")));
    }

    [TestMethod]
    public async Task Fix_AddDockerGroup_RefusesAnUnsafeUserName()
    {
        var r = Healthy().When("docker info", "", 1, "permission denied").When("whoami", "bob; rm -rf /\n");

        var result = await Sut(r).FixAsync("add-docker-group", default);

        Assert.IsFalse(result.Succeeded);
        Assert.IsFalse(r.Calls.Any(c => c.Contains("usermod")));
    }

    [TestMethod]
    public async Task Fix_ForSomethingThatIsNotBroken_DoesNothing()
    {
        var r = Healthy();

        var result = await Sut(r).FixAsync("install-git", default);

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(result.Message, "nothing to fix");
        Assert.IsFalse(r.Calls.Any(c => c.Contains("-u root")));
    }

    [TestMethod]
    public async Task Fix_WithAnUnknownId_IsRejected_AndRunsNothing()
    {
        var r = Healthy();

        var result = await Sut(r).FixAsync("format-c-drive", default);

        Assert.IsFalse(result.Succeeded);
        Assert.IsFalse(r.Calls.Any(c => c.Contains("-u root")));
    }

    [TestMethod]
    public async Task Fix_InstallDocker_UsesTheOfficialScript_AndSurfacesAFailure()
    {
        var r = Healthy().When("command -v docker", "", 1).When("get.docker.com", "", 100, "Could not resolve host");

        var result = await Sut(r).FixAsync("install-docker", default);

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(result.Output, "Could not resolve host");
        Assert.IsTrue(r.Calls.Any(c => c.Contains("-u root") && c.Contains("https://get.docker.com")));
    }

    [TestMethod]
    public async Task Fix_Git_InstallsWithApt()
    {
        var r = Healthy().When("git --version", "", 1);

        await Sut(r).FixAsync("install-git", default);

        Assert.IsTrue(r.Calls.Any(c => c.Contains("-u root") && c.Contains("apt-get install -y git")));
    }
}

