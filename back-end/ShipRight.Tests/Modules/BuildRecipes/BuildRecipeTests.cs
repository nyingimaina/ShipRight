using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using ShipRight.Modules.BuildRecipes;
using ShipRight.Modules.Projects;
using ShipRight.Shared.ProcessRunner;

namespace ShipRight.Tests.Modules.BuildRecipes;

/// <summary>
/// A service can say which Dockerfile, which stage and which build arguments make its image. Empty means exactly what
/// has always happened (docker build -t image:tag context), so nothing changes for any existing project.
/// </summary>
[TestClass]
public class BuildRecipeTests
{
    private static readonly string[] Plain = ["build", "--progress=plain", "-t", "shop/api:1.2.3", "D:\\src\\api"];

    private static ServiceConfig Svc(string? file = null, string? target = null, Dictionary<string, string>? args = null, string image = "shop/api") => new()
    {
        Name = "api", DockerImageName = image, BuildContextPath = "D:\\src\\api", DockerfilePath = file, BuildTarget = target, BuildArgs = args,
    };

    // ── nothing set means nothing changes ────────────────────────────────────

    [TestMethod]
    public void AnEmptyRecipe_LeavesTheCommandExactlyAsItWas()
    {
        CollectionAssert.AreEqual(Plain, DockerBuildRecipe.Apply(Plain, BuildRecipe.Of(Svc())).ToArray());
        CollectionAssert.AreEqual(Plain, DockerBuildRecipe.Apply(Plain, BuildRecipe.Of(Svc("", " ", new()))).ToArray(), "blank values count as empty");
    }

    [TestMethod]
    public void AnExistingServiceFromBeforeThisFeature_HasAnEmptyRecipe()
    {
        var old = JsonConvert.DeserializeObject<ServiceConfig>("{\"Name\":\"api\",\"DockerImageName\":\"shop/api\",\"BuildContextPath\":\"/src/api\"}")!;
        Assert.IsTrue(BuildRecipe.Of(old).IsEmpty);
        Assert.IsNull(old.DockerfilePath);
    }

    [TestMethod]
    public void TheRecipe_SurvivesSavingAndLoading()
    {
        var json = JsonConvert.SerializeObject(Svc("backend.Dockerfile", "prod", new() { ["NODE_ENV"] = "production" }));
        var back = JsonConvert.DeserializeObject<ServiceConfig>(json)!;
        Assert.AreEqual("backend.Dockerfile", back.DockerfilePath);
        Assert.AreEqual("prod", back.BuildTarget);
        Assert.AreEqual("production", back.BuildArgs!["NODE_ENV"]);
    }

    // ── what is added to the command ─────────────────────────────────────────

    [TestMethod]
    public void ADockerfileName_IsJoinedToTheContext_InTheContextsOwnStyle()
    {
        var win = DockerBuildRecipe.Apply(Plain, BuildRecipe.Of(Svc("backend.Dockerfile"))).ToArray();
        CollectionAssert.AreEqual(new[] { "build", "--progress=plain", "-f", "D:\\src\\api\\backend.Dockerfile", "-t", "shop/api:1.2.3", "D:\\src\\api" }, win);

        var linux = DockerBuildRecipe.Apply(["build", "-t", "shop/api:1", "/home/u/api"], BuildRecipe.Of(Svc("docker/Dockerfile.api"))).ToArray();
        CollectionAssert.AreEqual(new[] { "build", "-f", "/home/u/api/docker/Dockerfile.api", "-t", "shop/api:1", "/home/u/api" }, linux);
    }

    [TestMethod]
    public void AnAbsoluteDockerfilePath_IsUsedAsItIs()
    {
        var a = DockerBuildRecipe.Apply(["build", "-t", "x:1", "/ctx"], BuildRecipe.Of(Svc("/ops/api.Dockerfile"))).ToArray();
        CollectionAssert.Contains(a, "/ops/api.Dockerfile");
        var w = DockerBuildRecipe.Apply(["build", "-t", "x:1", "C:\\ctx"], BuildRecipe.Of(Svc("E:\\ops\\api.Dockerfile"))).ToArray();
        CollectionAssert.Contains(w, "E:\\ops\\api.Dockerfile");
    }

    [TestMethod]
    public void ATargetAndArguments_AreAdded_InAStableOrder()
    {
        var r = DockerBuildRecipe.Apply(Plain, BuildRecipe.Of(Svc(null, "prod", new() { ["ZED"] = "1", ["API_URL"] = "https://x.example/a=b" }))).ToArray();
        CollectionAssert.AreEqual(
            new[] { "build", "--progress=plain", "--target", "prod", "--build-arg", "API_URL=https://x.example/a=b", "--build-arg", "ZED=1", "-t", "shop/api:1.2.3", "D:\\src\\api" }, r);
    }

    [TestMethod]
    public void TheRecipeGoesBeforeTheTag_WhateverElseIsInTheCommand()
    {
        var cmd = new[] { "build", "--progress=plain", "--cache-from", "type=registry,ref=x:1", "-t", "shop/api:2", "/ctx" };
        var r = DockerBuildRecipe.Apply(cmd, BuildRecipe.Of(Svc("api.Dockerfile"))).ToList();
        Assert.IsTrue(r.IndexOf("-f") < r.IndexOf("-t"));
        CollectionAssert.AreEqual(cmd, r.Where(x => x != "-f" && x != "/ctx/api.Dockerfile").ToArray());
    }

    [TestMethod]
    public void CommandsThatAreNotABuild_AreNeverTouched()
    {
        string[] inspect = ["image", "inspect", "--format", "{{.Id}}", "shop/api:1"];
        CollectionAssert.AreEqual(inspect, DockerBuildRecipe.Apply(inspect, BuildRecipe.Of(Svc("x.Dockerfile"))).ToArray());
        string[] noTag = ["build", "/ctx"];
        CollectionAssert.AreEqual(noTag, DockerBuildRecipe.Apply(noTag, BuildRecipe.Of(Svc("x.Dockerfile"))).ToArray());
    }

    // ── what is allowed ──────────────────────────────────────────────────────

    [DataTestMethod]
    [DataRow("prod")]
    [DataRow("runtime-2")]
    [DataRow("stage.final")]
    public void ReasonableTargetsAreFine(string target) => Assert.AreEqual(0, BuildRecipe.Of(Svc(null, target)).Problems().Count);

    [DataTestMethod]
    [DataRow("pro d")]
    [DataRow("--rm")]
    [DataRow("a;b")]
    [DataRow("a\nb")]
    public void UnsafeTargetsAreRefused(string target)
    {
        var p = BuildRecipe.Of(Svc(null, target)).Problems();
        Assert.AreEqual(1, p.Count);
        StringAssert.Contains(p[0], "target");
    }

    [TestMethod]
    public void ArgumentNamesAndValues_AreChecked()
    {
        Assert.AreEqual(0, BuildRecipe.Of(Svc(null, null, new() { ["A_B1"] = "x y z", ["EMPTY"] = "" })).Problems().Count, "spaces and empty values are fine");
        Assert.AreEqual(1, BuildRecipe.Of(Svc(null, null, new() { ["1BAD"] = "x" })).Problems().Count);
        Assert.AreEqual(1, BuildRecipe.Of(Svc(null, null, new() { ["BAD-NAME"] = "x" })).Problems().Count);
        Assert.AreEqual(1, BuildRecipe.Of(Svc(null, null, new() { ["OK"] = "line1\nline2" })).Problems().Count);
    }

    [TestMethod]
    public void ADockerfilePath_WithControlCharacters_OrOptionLike_IsRefused()
    {
        Assert.AreEqual(1, BuildRecipe.Of(Svc("a\nb")).Problems().Count);
        Assert.AreEqual(1, BuildRecipe.Of(Svc("--help")).Problems().Count);
        Assert.AreEqual(0, BuildRecipe.Of(Svc("deploy/backend.Dockerfile")).Problems().Count);
    }

    [TestMethod]
    public void AnInvalidRecipe_StopsTheBuildWithAPlainMessage_InsteadOfBuildingTheWrongImage()
    {
        var ex = Assert.ThrowsException<InvalidOperationException>(() => DockerBuildRecipe.Apply(Plain, BuildRecipe.Of(Svc(null, "bad target"))));
        StringAssert.Contains(ex.Message, "build settings");
    }

    // ── the runner that applies it ───────────────────────────────────────────

    private sealed class CapturingRunner : IProcessRunner
    {
        public List<(string Exe, string[] Args, string? Dir, IReadOnlyDictionary<string, string>? Env, string? Stdin)> Calls { get; } = [];
        public Task<ProcessResult> RunAsync(string executable, string[] args, string? workingDir, Func<string, Task>? onOutput = null,
            Func<string, Task>? onError = null, CancellationToken ct = default, IReadOnlyDictionary<string, string>? envOverride = null,
            TimeSpan? timeout = null, string? stdin = null)
        {
            Calls.Add((executable, args, workingDir, envOverride, stdin));
            return Task.FromResult(new ProcessResult(0, "", "", TimeSpan.Zero));
        }
    }

    private sealed class MemoryProjects : IProjectStore
    {
        public Dictionary<string, ProjectConfig> All { get; } = [];
        public int Count => All.Count;
        public Task<ProjectConfig?> GetByNameAsync(string name) => Task.FromResult(All.Values.FirstOrDefault(p => p.Name == name));
        public Task<List<ProjectConfig>> GetAllAsync() => Task.FromResult(All.Values.ToList());
        public Task<ProjectConfig?> GetByIdAsync(string id) => Task.FromResult(All.GetValueOrDefault(id));
        public Task SaveAsync(ProjectConfig p) { All[p.Id] = p; return Task.CompletedTask; }
        public Task DeleteAsync(string id) { All.Remove(id); return Task.CompletedTask; }
    }

    private static async Task<(BuildRecipeProcessRunner Runner, CapturingRunner Inner, MemoryProjects Store)> Rig(params ServiceConfig[] services)
    {
        var store = new MemoryProjects();
        await store.SaveAsync(new ProjectConfig { Id = "p1", Name = "Shop", Services = services.ToList() });
        var inner = new CapturingRunner();
        return (new BuildRecipeProcessRunner(inner, store), inner, store);
    }

    [TestMethod]
    public async Task ABuildOfAServiceWithARecipe_GetsTheRecipe()
    {
        var (runner, inner, _) = await Rig(Svc("backend.Dockerfile", "prod"));
        await runner.RunAsync("docker", Plain, null);
        var args = inner.Calls.Single().Args;
        CollectionAssert.AreEqual(new[] { "build", "--progress=plain", "-f", "D:\\src\\api\\backend.Dockerfile", "--target", "prod", "-t", "shop/api:1.2.3", "D:\\src\\api" }, args);
    }

    [TestMethod]
    public async Task ABuildOfAServiceWithoutOne_IsPassedStraightThrough_WithEveryOtherArgument()
    {
        var (runner, inner, _) = await Rig(Svc());
        var env = new Dictionary<string, string> { ["DOCKER_BUILDKIT"] = "1" };
        await runner.RunAsync("docker", Plain, "/work", envOverride: env, stdin: "in");
        var call = inner.Calls.Single();
        CollectionAssert.AreEqual(Plain, call.Args);
        Assert.AreEqual("/work", call.Dir);
        Assert.AreSame(env, call.Env);
        Assert.AreEqual("in", call.Stdin);
    }

    [TestMethod]
    public async Task AnUnknownImage_AndEveryOtherCommand_AreLeftAlone()
    {
        var (runner, inner, _) = await Rig(Svc("x.Dockerfile"));
        await runner.RunAsync("docker", ["build", "-t", "other/app:1", "/ctx"], null);
        await runner.RunAsync("docker", ["push", "shop/api:1"], null);
        await runner.RunAsync("git", ["status"], null);
        await runner.RunAsync("docker", ["image", "inspect", "shop/api:1"], null);
        Assert.AreEqual(0, inner.Calls.Count(c => c.Args.Contains("-f")));
        Assert.AreEqual(4, inner.Calls.Count);
    }

    [TestMethod]
    public async Task ARegistryWithAPortInTheImageName_IsMatchedCorrectly()
    {
        var (runner, inner, _) = await Rig(Svc("a.Dockerfile", image: "localhost:5000/shop/api"));
        await runner.RunAsync("docker", ["build", "-t", "localhost:5000/shop/api:7", "/ctx"], null);
        CollectionAssert.Contains(inner.Calls.Single().Args, "/ctx/a.Dockerfile");
    }

    [TestMethod]
    public async Task TwoProjectsWithTheSameImageAndTheSameRecipe_AreFine()
    {
        var (runner, inner, store) = await Rig(Svc("a.Dockerfile"));
        await store.SaveAsync(new ProjectConfig { Id = "p2", Name = "Copy", Services = [Svc("a.Dockerfile")] });
        await runner.RunAsync("docker", Plain, null);
        CollectionAssert.Contains(inner.Calls.Single().Args, "D:\\src\\api\\a.Dockerfile");
    }

    [TestMethod]
    public async Task TwoProjectsWithTheSameImageButDifferentRecipes_StopTheBuild_NamingTheImage()
    {
        var (runner, _, store) = await Rig(Svc("a.Dockerfile"));
        await store.SaveAsync(new ProjectConfig { Id = "p2", Name = "Copy", Services = [Svc("b.Dockerfile")] });
        var ex = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => runner.RunAsync("docker", Plain, null));
        StringAssert.Contains(ex.Message, "shop/api");
    }

    [TestMethod]
    public async Task AServiceWithoutARecipe_BesideOneWithARecipeInAnotherProject_IsNotAmbiguous()
    {
        var (runner, inner, store) = await Rig(Svc());
        await store.SaveAsync(new ProjectConfig { Id = "p2", Name = "Copy", Services = [Svc("b.Dockerfile")] });
        await runner.RunAsync("docker", Plain, null);
        CollectionAssert.Contains(inner.Calls.Single().Args, "D:\\src\\api\\b.Dockerfile");
    }

    [TestMethod]
    public async Task TwoProjectsBuildingTheSameImageFromDifferentFolders_EachGetTheirOwnRecipe()
    {
        var (runner, inner, store) = await Rig(Svc("a.Dockerfile"));
        await store.SaveAsync(new ProjectConfig { Id = "p2", Name = "Copy", Services = [Svc("b.Dockerfile") with { BuildContextPath = "D:\\other" }] });

        await runner.RunAsync("docker", Plain, null);
        await runner.RunAsync("docker", ["build", "-t", "shop/api:1.2.3", "D:\\other"], null);

        CollectionAssert.Contains(inner.Calls[0].Args, "D:\\src\\api\\a.Dockerfile");
        CollectionAssert.Contains(inner.Calls[1].Args, "D:\\other\\b.Dockerfile");
    }

    [TestMethod]
    public async Task ThePreservingStore_AndTheRunner_CanBeAddedAroundExistingRegistrations()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        var made = new List<string>();
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<IProjectStore>(services, _ => { made.Add("inner"); return new MemoryProjects(); });
        services.DecorateSingleton<IProjectStore>((sp, inner) => { made.Add("outer"); return new BuildRecipePreservingProjectStore(inner); });

        var sp = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);
        var a = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<IProjectStore>(sp);
        var b = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<IProjectStore>(sp);

        Assert.IsInstanceOfType(a, typeof(BuildRecipePreservingProjectStore));
        Assert.AreSame(a, b, "still a single shared instance");
        CollectionAssert.AreEqual(new[] { "inner", "outer" }, made);
        await a.SaveAsync(new ProjectConfig { Id = "x", Name = "X" });
        Assert.AreEqual(1, a.Count);
    }

    [TestMethod]
    public void DecoratingSomethingThatWasNeverRegistered_SaysSo()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Assert.ThrowsException<InvalidOperationException>(() => services.DecorateSingleton<IProjectStore>((sp, inner) => inner));
    }

    // ── saving from the original wizard must not wipe it ─────────────────────

    [TestMethod]
    public async Task AnOrdinaryEditFromTheOldWizard_KeepsTheRecipe_ButASentValueWins()
    {
        var inner = new MemoryProjects();
        await inner.SaveAsync(new ProjectConfig { Id = "p1", Name = "Shop", Services = [Svc("backend.Dockerfile", "prod", new() { ["A"] = "1" })] });
        var store = new BuildRecipePreservingProjectStore(inner);

        await store.SaveAsync(new ProjectConfig { Id = "p1", Name = "Shop renamed", Services = [Svc()] });
        var kept = (await inner.GetByIdAsync("p1"))!.Services[0];
        Assert.AreEqual("backend.Dockerfile", kept.DockerfilePath);
        Assert.AreEqual("prod", kept.BuildTarget);
        Assert.AreEqual("1", kept.BuildArgs!["A"]);

        await store.SaveAsync(new ProjectConfig { Id = "p1", Name = "Shop", Services = [Svc("other.Dockerfile")] });
        Assert.AreEqual("other.Dockerfile", (await inner.GetByIdAsync("p1"))!.Services[0].DockerfilePath);
    }

    [TestMethod]
    public async Task AnEmptyValue_ClearsARecipeOnPurpose()
    {
        var inner = new MemoryProjects();
        await inner.SaveAsync(new ProjectConfig { Id = "p1", Name = "Shop", Services = [Svc("backend.Dockerfile", "prod", new() { ["A"] = "1" })] });
        await new BuildRecipePreservingProjectStore(inner).SaveAsync(new ProjectConfig { Id = "p1", Name = "Shop", Services = [Svc("", "", new())] });

        Assert.IsTrue(BuildRecipe.Of((await inner.GetByIdAsync("p1"))!.Services[0]).IsEmpty);
    }

    [TestMethod]
    public async Task NewProjectsAndNewServices_AreSavedAsTheyAre()
    {
        var inner = new MemoryProjects();
        var store = new BuildRecipePreservingProjectStore(inner);
        await store.SaveAsync(new ProjectConfig { Id = "p1", Name = "Shop", Services = [Svc("a.Dockerfile")] });
        Assert.AreEqual("a.Dockerfile", inner.All["p1"].Services[0].DockerfilePath);

        await store.SaveAsync(inner.All["p1"] with { Services = [Svc("a.Dockerfile"), Svc() with { Name = "web", DockerImageName = "shop/web" }] });
        Assert.IsNull(inner.All["p1"].Services[1].DockerfilePath);
    }

    [TestMethod]
    public async Task EverythingElseIsPassedStraightToTheStoreItWraps()
    {
        var inner = new MemoryProjects();
        await inner.SaveAsync(new ProjectConfig { Id = "p1", Name = "Shop" });
        var store = new BuildRecipePreservingProjectStore(inner);
        Assert.AreEqual(1, store.Count);
        Assert.AreEqual("Shop", (await store.GetByIdAsync("p1"))!.Name);
        Assert.AreEqual("p1", (await store.GetByNameAsync("Shop"))!.Id);
        Assert.AreEqual(1, (await store.GetAllAsync()).Count);
        await store.DeleteAsync("p1");
        Assert.AreEqual(0, inner.Count);
    }
}
