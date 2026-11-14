using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.JitDeploy;
using ShipRight.Modules.Projects;

namespace ShipRight.Tests.Modules.JitDeploy;

/// <summary>
/// ShipRight can hold each service's Dockerfile, build folder, stage and arguments itself and hand them to compose in a small
/// generated file next to the deploy state on the server, so the person's own compose file needs no changes. The same file sets each
/// image's name and its release tag, so there is nothing to type for tagging.
/// </summary>
[TestClass]
public class JitComposeOverrideTests
{
    private static readonly JitTarget Target = new("h", "u", "/k", "/home/deploy/shop");

    private static JitService Svc(string name, string image, string? dockerfile = "Dockerfile", string context = ".", string? target = null, Dictionary<string, string>? args = null) =>
        new(name, name.ToUpperInvariant() + "_TAG", image, dockerfile is null ? null : new JitBuildRecipe(context, dockerfile, target, args));

    // ── the generated file ───────────────────────────────────────────────────

    [TestMethod]
    public void EachServiceWithARecipe_GetsABuildSection_AndItsOwnImageWithItsOwnTagVariable()
    {
        var yaml = ComposeOverride.Render("docker-compose.yml",
        [
            Svc("backend", "reg/herd-backend", "backend.Dockerfile"),
            Svc("frontend", "reg/herd-frontend", "frontend.Dockerfile", target: "runner", args: new() { ["NEXT_PUBLIC_API_URL"] = "https://api.x.example" }),
        ])!.Replace("\r\n", "\n");

        StringAssert.Contains(yaml, "services:\n  \"backend\":\n    build:\n      context: \".\"\n      dockerfile: \"backend.Dockerfile\"\n    image: \"${BACKEND_TAG:-reg/herd-backend:latest}\"\n");
        StringAssert.Contains(yaml, "  \"frontend\":\n    build:\n      context: \".\"\n      dockerfile: \"frontend.Dockerfile\"\n      target: \"runner\"\n      args:\n        \"NEXT_PUBLIC_API_URL\": \"https://api.x.example\"\n    image: \"${FRONTEND_TAG:-reg/herd-frontend:latest}\"\n");
    }

    [TestMethod]
    public void WithNoRecipeAtAll_ThereIsNoFile() =>
        Assert.IsNull(ComposeOverride.Render("docker-compose.yml", [Svc("api", "shop/api", dockerfile: null)]));

    [TestMethod]
    public void OnlyTheServicesWithARecipe_AreWritten()
    {
        var yaml = ComposeOverride.Render("docker-compose.yml", [Svc("api", "shop/api"), Svc("web", "shop/web", dockerfile: null)])!;
        StringAssert.Contains(yaml, "\"api\"");
        Assert.IsFalse(yaml.Contains("\"web\""));
    }

    [TestMethod]
    public void PathsAreWrittenRelativeToTheComposeFilesFolder_AndTheDockerfileRelativeToItsContext()
    {
        var yaml = ComposeOverride.Render("deploy/docker-compose.yml", [Svc("api", "shop/api", "services/api/Dockerfile.prod", "services/api")])!.Replace("\r\n", "\n");
        StringAssert.Contains(yaml, "context: \"../services/api\"");
        StringAssert.Contains(yaml, "dockerfile: \"Dockerfile.prod\"");

        var outside = ComposeOverride.Render("docker-compose.yml", [Svc("api", "shop/api", "ops/api.Dockerfile", "api")])!;
        StringAssert.Contains(outside, "context: \"api\"");
        StringAssert.Contains(outside, "dockerfile: \"../ops/api.Dockerfile\"");
    }

    [TestMethod]
    public void AwkwardArgumentValues_AreQuotedSafely()
    {
        var yaml = ComposeOverride.Render("docker-compose.yml", [Svc("api", "shop/api", args: new() { ["A"] = "say \"hi\" \\ $HOME #not-a-comment: x" })])!;
        StringAssert.Contains(yaml, "\"A\": \"say \\u0022hi\\u0022 \\\\ $HOME #not-a-comment: x\"");
    }

    [TestMethod]
    public void NothingNamesTheDeployer_AndItIsClearlyGenerated()
    {
        var yaml = ComposeOverride.Render("docker-compose.yml", [Svc("api", "shop/api")])!;
        StringAssert.StartsWith(yaml, "# ");
        Assert.IsFalse(yaml.Contains("shipright", StringComparison.OrdinalIgnoreCase));
    }

    // ── what is allowed ──────────────────────────────────────────────────────

    [DataTestMethod]
    [DataRow("../etc/Dockerfile", ".")]
    [DataRow("/abs/Dockerfile", ".")]
    [DataRow("a b/Dockerfile", ".")]
    [DataRow("Dockerfile", "../x")]
    [DataRow("Dockerfile", "a;b")]
    [DataRow("a\nb", ".")]
    [DataRow("Dockerfile", "")]
    public void UnsafePathsAreRefused(string dockerfile, string context) =>
        Assert.ThrowsException<ArgumentException>(() => ComposeOverride.Render("docker-compose.yml", [Svc("api", "shop/api", dockerfile, context)]));

    [TestMethod]
    public void UnsafeStagesAndArgumentsAreRefused_WithoutEchoingValues()
    {
        Assert.ThrowsException<ArgumentException>(() => ComposeOverride.Render("docker-compose.yml", [Svc("api", "shop/api", target: "--rm")]));
        Assert.ThrowsException<ArgumentException>(() => ComposeOverride.Render("docker-compose.yml", [Svc("api", "shop/api", args: new() { ["BAD-NAME"] = "x" })]));
        var ex = Assert.ThrowsException<ArgumentException>(() => ComposeOverride.Render("docker-compose.yml", [Svc("api", "shop/api", args: new() { ["OK"] = "line1\nsecret-line2" })]));
        Assert.IsFalse(ex.Message.Contains("secret-line2"));
    }

    // ── writing it on the server ─────────────────────────────────────────────

    [TestMethod]
    public void TheFileLivesBesideTheDeployState_NotInTheRepository() =>
        Assert.AreEqual("/home/deploy/shop/.deploy-state/compose.override.yml", ComposeOverride.Path(Target));

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
    public void TheWriteCommand_ParsesAsRealShell_AndWritesTheExactTextAtomically()
    {
        if (Bash() is null) Assert.Inconclusive("bash is not available.");
        var dir = "/tmp/ovr-" + Guid.NewGuid().ToString("N");
        var t = new JitTarget("h", "u", "/k", dir);
        var yaml = ComposeOverride.Render("docker-compose.yml", [Svc("api", "shop/api", args: new() { ["A"] = "it's \"quoted\" $x" })])!;
        var cmd = ComposeOverride.WriteCommand(t, yaml);

        Assert.AreEqual(0, Run("-n", cmd).Exit);
        var (exit, _, err) = Run("-s", cmd);
        Assert.AreEqual(0, exit, err);
        var (_, content, _) = Run("-s", $"cat '{dir}/.deploy-state/compose.override.yml'; rm -rf '{dir}'");
        Assert.AreEqual(yaml.Replace("\r\n", "\n"), content.Replace("\r\n", "\n"));
    }

    // ── the compose commands carry both files ────────────────────────────────

    private static readonly IReadOnlyList<TagBinding> Bindings = [new TagBinding("API_TAG", "shop/api")];

    [TestMethod]
    public void WithAnOverride_EveryComposeCommandUsesTheBaseFileAndThen_TheOverride()
    {
        var ctx = new ComposeContext(Target, "shop", null, "deploy/docker-compose.yml", ComposeOverride.Path(Target));
        foreach (var c in new[] { JitCommands.ComposeConfig(ctx, Bindings, "r1-0000000"), JitCommands.Build(ctx, ["api"], Bindings, "r1-0000000"), JitCommands.StartService(ctx, "api", Bindings, "r1-0000000") })
            StringAssert.Contains(c, "-p 'shop' -f 'deploy/docker-compose.yml' -f '/home/deploy/shop/.deploy-state/compose.override.yml' ", c);
    }

    [TestMethod]
    public void AnOverrideWithoutABaseFile_IsRefused() =>
        Assert.ThrowsException<ArgumentException>(() => JitCommands.Build(new ComposeContext(Target, "shop", null, null, ComposeOverride.Path(Target)), ["api"], Bindings, "r1-0000000"));

    // ── the deploy ───────────────────────────────────────────────────────────

    private static JitDeployRequest Req(string? composeFile = "docker-compose.yml", params JitService[] services) => new()
    {
        Target = Target, Project = "shop", RepoUrl = "git@github.com:o/r.git", Branch = "master", ComposeFile = composeFile,
        Services = services.Length > 0 ? services : [Svc("api", "shop/api", "api.Dockerfile"), Svc("web", "shop/web", "web.Dockerfile")],
    };

    private static async Task<(JitDeployResult R, FakeRemote Remote)> Deploy(JitDeployRequest req, Action<FakeRemote>? setup = null)
    {
        var remote = new FakeRemote(); setup?.Invoke(remote);
        var r = await new JitDeployer(remote, new FakeClock(), new JitDeployLock()).DeployAsync(req, _ => Task.CompletedTask);
        return (r, remote);
    }

    [TestMethod]
    public async Task TheFileIsWrittenAfterTheCodeIsFetched_AndBeforeCompose_IsAskedAnything()
    {
        var (r, remote) = await Deploy(Req());
        Assert.AreEqual(JitOutcome.Deployed, r.Outcome, r.Message);

        StringAssert.Contains(remote.OverrideYaml!, "\"api\"");
        StringAssert.Contains(remote.OverrideYaml!, "dockerfile: \"api.Dockerfile\"");
        var events = remote.Events;
        Assert.IsTrue(events.IndexOf("checkout:aaaaaaa") < events.IndexOf("override-written") || events.FindIndex(e => e.StartsWith("checkout")) < events.IndexOf("override-written"));
        Assert.IsTrue(events.IndexOf("override-written") < events.FindIndex(e => e.StartsWith("build:")));
    }

    [TestMethod]
    public async Task EveryComposeCommandOfTheDeploy_UsesBothFiles()
    {
        var (_, remote) = await Deploy(Req());
        var compose = remote.Commands.Where(c => c.Contains("docker compose -p")).ToList();
        Assert.IsTrue(compose.Count > 5);
        Assert.IsTrue(compose.All(c => c.Contains("-f 'docker-compose.yml' -f '/home/deploy/shop/.deploy-state/compose.override.yml'")));
    }

    [TestMethod]
    public async Task WithoutRecipes_NothingIsWritten_AndNothingChanges()
    {
        var (r, remote) = await Deploy(Req("docker-compose.yml", Svc("api", "shop/api", null), Svc("web", "shop/web", null)));
        Assert.AreEqual(JitOutcome.Deployed, r.Outcome, r.Message);
        Assert.IsNull(remote.OverrideYaml);
        Assert.IsFalse(remote.Commands.Any(c => c.Contains("compose.override.yml")));
    }

    [TestMethod]
    public async Task ARecipeWithNoComposeFileChosen_IsRefusedBeforeTheServerIsContacted()
    {
        var (r, remote) = await Deploy(Req(null));
        Assert.AreEqual(JitOutcome.FailedBeforeChange, r.Outcome);
        StringAssert.Contains(r.Message, "compose file");
        Assert.AreEqual(0, remote.Commands.Count);
    }

    [TestMethod]
    public async Task AnUnsafeRecipe_IsRefusedBeforeTheServerIsContacted()
    {
        var (r, remote) = await Deploy(Req("docker-compose.yml", Svc("api", "shop/api", "../../etc/passwd")));
        Assert.AreEqual(JitOutcome.FailedBeforeChange, r.Outcome);
        Assert.AreEqual(0, remote.Commands.Count);
    }

    [TestMethod]
    public async Task AServiceWithNeitherARecipeNorABuildSection_IsToldHowToFixIt()
    {
        var (r, _) = await Deploy(Req("docker-compose.yml", Svc("api", "shop/api", "api.Dockerfile"), Svc("web", "shop/web", null)),
            f => f.ComposeJsonFor = _ => """{"services":{"api":{"build":{"context":"."},"image":"shop/api:r1-aaaaaaa"},"web":{"image":"shop/web:latest"}}}""");

        Assert.AreEqual(JitOutcome.FailedBeforeChange, r.Outcome);
        StringAssert.Contains(r.Message, "has no build: section");
        StringAssert.Contains(r.Message, "Browse for Dockerfile");
    }

    [TestMethod]
    public async Task TheDeployKeyAndTheCredential_StillGoWithTheGitCommandsOnly()
    {
        var (_, remote) = await Deploy(Req() with { DeployKeyName = "deploy_shop" });
        Assert.AreEqual(2, remote.Commands.Count(c => c.StartsWith("export GIT_SSH_COMMAND=")));
        Assert.IsFalse(remote.Commands.Any(c => c.Contains("compose.override.yml") && c.Contains("GIT_SSH_COMMAND")));
    }

    // ── the settings ─────────────────────────────────────────────────────────

    private static ProjectConfig Project() => new()
    {
        Id = "p1", Name = "Herd",
        Server = new ServerConfig { Host = "h", Username = "u", SshKeyPath = "/k", RemoteWorkingDir = "/home/u/herd" },
    };

    private static JitDeployConfig Cfg(string composeFile, params JitConfiguredService[] services) =>
        new() { Enabled = true, RepoUrl = "git@h:o/r.git", Branch = "main", ComposeFile = composeFile, ComposeServices = services };

    [TestMethod]
    public void AConfiguredServiceWithADockerfile_BecomesARecipe()
    {
        var r = JitDeployMapper.Map(Project(), Cfg("docker-compose.yml",
            new JitConfiguredService("backend", "reg/herd-backend", "BACKEND_TAG", "backend.Dockerfile", ".", "runner", new() { ["A"] = "1" })));
        var s = r.Request!.Services.Single();
        Assert.AreEqual(("backend.Dockerfile", ".", "runner"), (s.Recipe!.Dockerfile, s.Recipe.Context, s.Recipe.Target));
        Assert.AreEqual("1", s.Recipe.Args!["A"]);
    }

    [TestMethod]
    public void AConfiguredServiceWithNoContext_BuildsFromTheRepositoryRoot()
    {
        var s = JitDeployMapper.Map(Project(), Cfg("docker-compose.yml", new JitConfiguredService("backend", "reg/b", "BACKEND_TAG", "backend.Dockerfile"))).Request!.Services.Single();
        Assert.AreEqual(".", s.Recipe!.Context);
    }

    [TestMethod]
    public void RecipesWithNoComposeFileChosen_AreAProblemWithAPlainFix()
    {
        var r = JitDeployMapper.Map(Project(), Cfg("", new JitConfiguredService("backend", "reg/b", "BACKEND_TAG", "backend.Dockerfile")));
        Assert.IsNull(r.Request);
        Assert.IsTrue(r.Problems.Any(p => p.Contains("Pick the compose file")));
    }

    [TestMethod]
    public void EveryServiceTakenFromTheComposeFile_NeedsItsDockerfile()
    {
        var p = JitDeployConfigValidator.Validate(Cfg("docker-compose.yml", new JitConfiguredService("backend", "reg/b", "BACKEND_TAG")));
        Assert.IsTrue(p.Any(x => x.Field == "composeServices" && x.Message.Contains("Pick the Dockerfile for 'backend'")));
        Assert.AreEqual(0, JitDeployConfigValidator.Validate(Cfg("docker-compose.yml", new JitConfiguredService("backend", "reg/b", "BACKEND_TAG", "backend.Dockerfile"))).Count(x => x.Field == "composeServices"));
    }

    [TestMethod]
    public void TheRecipeFieldsAreCheckedWhenSaving()
    {
        var bad = JitDeployConfigValidator.Validate(Cfg("docker-compose.yml", new JitConfiguredService("backend", "reg/b", "BACKEND_TAG", "../x", ".")));
        Assert.IsTrue(bad.Any(x => x.Field == "composeServices"));
    }

    [TestMethod]
    public void TheRecipeSurvivesSavingAndLoading()
    {
        var cfg = Cfg("docker-compose.yml", new JitConfiguredService("backend", "reg/b", "BACKEND_TAG", "backend.Dockerfile", ".", "runner", new() { ["A"] = "1" }));
        var back = Newtonsoft.Json.JsonConvert.DeserializeObject<JitDeployConfig>(Newtonsoft.Json.JsonConvert.SerializeObject(cfg))!.ComposeServices.Single();
        Assert.AreEqual(("backend.Dockerfile", "runner"), (back.Dockerfile, back.Target));
        Assert.AreEqual("1", back.Args!["A"]);
        var old = Newtonsoft.Json.JsonConvert.DeserializeObject<JitDeployConfig>("{\"ComposeServices\":[{\"Name\":\"a\",\"ImageRef\":\"x/a\",\"TagVariable\":\"A_TAG\"}]}")!;
        Assert.IsNull(old.ComposeServices.Single().Dockerfile, "settings saved before this have no Dockerfile yet");
    }
}
