using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.JitDeploy;
using ShipRight.Tests.Modules.Setup;

namespace ShipRight.Tests.Modules.JitDeploy;

/// <summary>
/// A deploy that fails before it starts must still leave something a person can download and send on. These tests pin
/// the text, the redaction, the project scoping and the bounded retention of that record.
/// </summary>
[TestClass]
public class JitDeployAttemptsTests
{
    private static JitAttemptContent Content(
        JitGuardReport? guard = null, IReadOnlyList<string>? warnings = null, IReadOnlyList<string>? problems = null,
        string? buildLog = null) => new(
        "a1", new DateTime(2026, 11, 27, 8, 0, 0, DateTimeKind.Utc), "p1", "Shop", "Nyangi", true,
        "deploy@server (/home/deploy/shop)", "git@github.com:o/r.git", "master", "3f2a9c1d0e8b",
        null, guard, warnings ?? [], problems ?? [], [], "blocked", "Commit your local changes.", "note", buildLog);

    private static JitGuardReport Guard(bool applicable = true, string? problem = null, params JitRepoProbe[] probes) =>
        new(applicable, "master", probes.FirstOrDefault()?.Path, probes, probes.SelectMany(p => p.Warnings).ToList(), problem);

    private static JitRepoProbe Probe(string? problem = null, IReadOnlyList<string>? warnings = null) => new(
        @"D:\work\app", "git@github.com:o/r.git", "master", true, true,
        ["back-end/ShipRight.Server/wwwroot/index.html"], ["scratch.sql"], "3f2a9c1d", "3f2a9c1d", 0, 0,
        warnings ?? [], problem);

    [TestMethod]
    public async Task WritesAndReadsOneAttemptPerProject()
    {
        using var tree = new TempTree();
        var store = new JitDeployAttemptStore(tree.Root);
        var id = JitDeployAttemptStore.NewId(DateTime.UtcNow);

        await store.WriteAsync("p1", id, "hello");

        Assert.AreEqual("hello", await store.ReadAsync("p1", id));
        Assert.IsNull(await store.ReadAsync("p2", id), "a project cannot read another project's attempt");
    }

    [TestMethod]
    public async Task UnsafeIdsAreNeverUsedAsAPath()
    {
        using var tree = new TempTree();
        var store = new JitDeployAttemptStore(tree.Root);

        await Assert.ThrowsExceptionAsync<ArgumentException>(async () => await store.WriteAsync("p1", "../escape", "x"));
        Assert.IsNull(await store.ReadAsync("p1", "..\\escape"));
        Assert.IsFalse(JitDeployAttemptStore.IsSafeId("../escape"));
    }

    [TestMethod]
    public void ProjectFolderCannotEscapeAndStillSeparatesLookalikes()
    {
        var a = JitDeployAttemptStore.ProjectKey("Acme/One");
        var b = JitDeployAttemptStore.ProjectKey("acme-one");

        Assert.IsFalse(a.Contains('/') || a.Contains('\\') || a.Contains(".."));
        Assert.AreNotEqual(a, b);
    }

    [TestMethod]
    public void RenderNamesEveryOffendingFileAndTheDecision()
    {
        var text = JitAttemptText.Render(Content(
            guard: Guard(problem: "Commit your local changes before deploying (index.html).",
                probes: Probe(problem: "Commit your local changes before deploying (index.html).",
                    warnings: [@"Untracked files in D:\work\app are not part of any commit and will not be on the server: scratch.sql."])),
            problems: ["Commit your local changes before deploying (index.html)."]), []);

        StringAssert.Contains(text, "Result: blocked");
        StringAssert.Contains(text, @"D:\work\app");
        StringAssert.Contains(text, "back-end/ShipRight.Server/wwwroot/index.html");
        StringAssert.Contains(text, "scratch.sql");
        StringAssert.Contains(text, "will not be on the server");
        StringAssert.Contains(text, "Commit your local changes before deploying");
        StringAssert.Contains(text, "Mode: Dry run");
    }

    [TestMethod]
    public void RenderSaysWhenNoLocalCheckoutMatched()
    {
        var text = JitAttemptText.Render(Content(guard: Guard(applicable: false)), []);

        StringAssert.Contains(text, "No local checkout matched");
    }

    [TestMethod]
    public void RenderRedactsTheSavedCredentialAndAnyTokenInTheText()
    {
        var token = "ghp_secretsecretsecret123456789";
        var content = Content(problems: [$"Could not use {token}"]) with { RepoUrl = $"https://token:{token}@github.com/o/r.git" };

        var text = JitAttemptText.Render(content, [token]);

        Assert.IsFalse(text.Contains(token, StringComparison.Ordinal));
        StringAssert.Contains(text, "***");
    }

    [TestMethod]
    public void RenderIncludesTheBuildLogWhenThereIsOne()
    {
        var text = JitAttemptText.Render(Content(buildLog: "[deploy] Release r1-abc1234 is live."), []);

        StringAssert.Contains(text, "Deploy log");
        StringAssert.Contains(text, "Release r1-abc1234 is live.");
    }

    [TestMethod]
    public async Task RetentionKeepsTheNewestAttemptsAndDeletesTheRest()
    {
        using var tree = new TempTree();
        var store = new JitDeployAttemptStore(tree.Root);
        var started = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        for (var i = 0; i < JitDeployAttemptStore.Retained + 3; i++)
        {
            var id = JitDeployAttemptStore.NewId(started.AddSeconds(i));
            await store.WriteAsync("p1", id, i.ToString());
        }

        var dir = Path.Combine(tree.Root, "jit-deploy", "attempts", JitDeployAttemptStore.ProjectKey("p1"));
        Assert.AreEqual(JitDeployAttemptStore.Retained, Directory.GetFiles(dir, "*.log").Length);
    }
}
