using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.JitDeploy;

namespace ShipRight.Tests.Modules.JitDeploy;

/// <summary>
/// A Dockerfile that uses a BuildKit feature (RUN --mount ...) needs Docker to build with BuildKit. That is the server's job, not the
/// developer's: ShipRight checks for it, installs the missing piece (buildx) like the other tools, asks for BuildKit explicitly, and
/// explains a failure in plain words.
/// </summary>
[TestClass]
public class JitBuildKitTests
{
    private static readonly JitTarget Target = new("h", "u", "/k", "/home/deploy/shop");

    // ── detection and the plan ───────────────────────────────────────────────

    [TestMethod]
    public void TheCheck_LooksForBuildxWithoutChangingAnything()
    {
        var c = JitServerPrep.Detect();
        StringAssert.Contains(c, "docker buildx version");
        StringAssert.Contains(c, "missing:buildx");
        Assert.IsFalse(c.Contains("sudo"));
    }

    [TestMethod]
    public void BuildxIsReadFromTheAnswer() =>
        CollectionAssert.AreEqual(new[] { ServerTool.Buildx }, JitServerPrep.ParseMissing(["missing:buildx"]).ToArray());

    private static string[] PlanOf(params ServerTool[] missing) => JitServerPrep.Plan(missing).Select(s => s.Tool.ToString()).ToArray();

    [TestMethod]
    public void WhenOnlyBuildxIsMissing_OnlyItIsInstalled() =>
        CollectionAssert.AreEqual(new[] { "Buildx" }, PlanOf(ServerTool.Buildx));

    [TestMethod]
    public void WithNoDocker_BuildxComesLast_AfterComposeAndTheStart() =>
        CollectionAssert.AreEqual(new[] { "Docker", "Daemon", "Compose", "Buildx" }, PlanOf(ServerTool.Docker));

    [TestMethod]
    public void TheStepHasAPlainTitle()
    {
        var s = JitServerPrep.Plan([ServerTool.Buildx]).Single();
        StringAssert.Contains(s.Title, "BuildKit");
        StringAssert.Contains(JitServerPrep.Describe(ServerTool.Buildx), "BuildKit");
    }

    // ── the install command ──────────────────────────────────────────────────

    private static string Install() => JitServerPrep.Plan([ServerTool.Buildx]).Single().Command;

    [TestMethod]
    public void TheInstallCommand_ChecksFirst_ExplainsSudo_AndOnlyAddsThePlugin()
    {
        var c = Install();
        StringAssert.Contains(c, "docker buildx version");
        StringAssert.Contains(c, "passwordless sudo");
        StringAssert.Contains(c, "/usr/local/lib/docker/cli-plugins/docker-buildx");
        StringAssert.Contains(c, "docker/buildx/releases");
        Assert.IsFalse(c.Contains("prune") || c.Contains("rm -rf") || c.Contains("shipright", StringComparison.OrdinalIgnoreCase));
    }

    private static string? Bash()
    {
        foreach (var p in new[] { @"C:\Program Files\Git\bin\bash.exe", "/bin/bash", "/usr/bin/bash" })
            if (File.Exists(p)) return p;
        return null;
    }

    private static (int Exit, string Out, string Err) Run(string args, string stdin)
    {
        var psi = new ProcessStartInfo(Bash()!, args) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        p.StandardInput.Write(stdin);
        p.StandardInput.Close();
        var o = p.StandardOutput.ReadToEnd(); var e = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, o, e);
    }

    [TestMethod]
    public void TheInstallCommand_ParsesAsRealShell()
    {
        if (Bash() is null) Assert.Inconclusive("bash is not available.");
        Assert.AreEqual(0, Run("-n", Install()).Exit);
    }

    [TestMethod]
    public void WhenBuildxIsAlreadyThere_TheInstallDoesNothing()
    {
        if (Bash() is null) Assert.Inconclusive("bash is not available.");
        var (exit, output, err) = Run("-s", "docker() { return 0; }\n" + Install());
        Assert.AreEqual(0, exit, err);
        StringAssert.Contains(output, "already installed");
    }

    // ── the build asks for BuildKit ──────────────────────────────────────────

    [TestMethod]
    public void TheBuildCommand_AsksForBuildKitExplicitly_BeforeCompose()
    {
        var ctx = new ComposeContext(Target, "shop");
        var c = JitCommands.Build(ctx, ["api"], [new TagBinding("API_TAG", "shop/api")], "r1-0000000");
        var kit = c.IndexOf("export DOCKER_BUILDKIT=1", StringComparison.Ordinal);
        Assert.IsTrue(kit > 0, c);
        Assert.IsTrue(kit < c.IndexOf("docker compose", StringComparison.Ordinal));
        StringAssert.Contains(c, "docker compose -p 'shop' build --progress=plain 'api'");
    }

    [TestMethod]
    public void OtherCommands_AreNotChanged()
    {
        var ctx = new ComposeContext(Target, "shop");
        Assert.IsFalse(JitCommands.StopService(ctx, "api", 30, [new TagBinding("API_TAG", "shop/api")], "r1-0000000").Contains("BUILDKIT"));
    }

    // ── the explanation ──────────────────────────────────────────────────────

    [DataTestMethod]
    [DataRow("the --mount option requires BuildKit. Refer to https://docs.docker.com/go/buildkit/ to learn how to build images with BuildKit enabled")]
    [DataRow("ERROR: BuildKit is enabled but the buildx component is missing or broken.")]
    [DataRow("RUN --mount=type=cache ... failed: requires buildkit")]
    public void ABuildKitFailure_IsExplained_AsTheServersToolingNotTheDockerfile(string line)
    {
        var advice = JitBuildAdvice.For(["Step 4/20 : RUN dotnet restore", line]);
        StringAssert.Contains(advice, "not a problem in your Dockerfile");
        StringAssert.Contains(advice, "Deploy again");
        StringAssert.Contains(advice, "docker buildx version");
    }

    [TestMethod]
    public void AnyOtherFailure_GetsNoAdvice()
    {
        Assert.AreEqual("", JitBuildAdvice.For(["ERROR: failed to solve: process \"/bin/sh -c npm ci\" did not complete successfully"]));
        Assert.AreEqual("", JitBuildAdvice.For([]));
    }

    private static JitDeployRequest Req() => new()
    {
        Target = Target, Project = "shop", RepoUrl = "git@github.com:o/r.git", Branch = "master",
        Services = [new JitService("api", "API_TAG", "shop/api"), new JitService("web", "WEB_TAG", "shop/web")],
    };

    [TestMethod]
    public async Task TheDeployResult_CarriesTheExplanation_AndStillChangesNothing()
    {
        var remote = new FakeRemote { BuildOk = false, BuildFailureLines = ["Step 4/20 : RUN --mount=type=cache dotnet restore", "the --mount option requires BuildKit. Refer to https://docs.docker.com/go/buildkit/ to learn how to build images with BuildKit enabled"] };
        var r = await new JitDeployer(remote, new FakeClock(), new JitDeployLock()).DeployAsync(Req(), _ => Task.CompletedTask);

        Assert.AreEqual(JitOutcome.FailedBeforeChange, r.Outcome);
        StringAssert.Contains(r.Message, "The build failed, so nothing was changed");
        StringAssert.Contains(r.Message, "the --mount option requires BuildKit");
        StringAssert.Contains(r.Message, "not a problem in your Dockerfile");
        Assert.IsFalse(remote.Events.Any(e => e.StartsWith("stop:")));
    }

    [TestMethod]
    public async Task AnOrdinaryBuildFailure_KeepsItsOldMessage()
    {
        var remote = new FakeRemote { BuildOk = false };
        var r = await new JitDeployer(remote, new FakeClock(), new JitDeployLock()).DeployAsync(Req(), _ => Task.CompletedTask);
        StringAssert.Contains(r.Message, "The build failed, so nothing was changed");
        Assert.IsFalse(r.Message.Contains("not a problem in your Dockerfile"));
    }

    [TestMethod]
    public async Task AFreshServer_GetsBuildxInstalledLikeTheOtherTools()
    {
        var remote = new FakeRemote();
        remote.MissingTools.Add(ServerTool.Buildx);
        var r = await new JitDeployer(remote, new FakeClock(), new JitDeployLock()).DeployAsync(Req(), _ => Task.CompletedTask);
        Assert.AreEqual(JitOutcome.Deployed, r.Outcome, r.Message);
        CollectionAssert.Contains(remote.Events, "prepare:buildx");
    }
}
