using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.JitDeploy;

namespace ShipRight.Tests.Modules.JitDeploy;

[TestClass]
public class JitBuildFingerprintTests
{
    private const string Repo = "/home/deploy/shop/repo";
    private static readonly JitTarget Target = new("h", "u", "/k", "/home/deploy/shop");
    private const string NewSha = "ab8b4a9f3c1d2e5f6a7b8c9d0e1f2a3b4c5d6e7f";
    private const string OldSha = "1111111f3c1d2e5f6a7b8c9d0e1f2a3b4c5d6e7f";

    private static string Config(string api = "{\"context\":\"/home/deploy/shop/repo/api\",\"dockerfile\":\"Dockerfile\",\"args\":{\"B\":\"2\",\"A\":\"1\"},\"target\":\"prod\"}") =>
        "{\"name\":\"shop\",\"services\":{\"db\":{\"image\":\"postgres:16\"},\"api\":{\"image\":\"shop/api:r2-aaa\",\"build\":" + api + "}}}";

    // ── reading build inputs from the rendered compose file ───────────────────

    [TestMethod]
    public void Reads_the_folder_dockerfile_and_settings_of_each_buildable_service()
    {
        var r = BuildInputsReader.Read(Config(), Repo);
        Assert.IsFalse(r.ContainsKey("db"), "a service with no build has nothing to fingerprint");
        var i = r["api"].Inputs!;
        Assert.AreEqual("api", i.ContextRel);
        Assert.AreEqual("api/Dockerfile", i.DockerfileRel);
        StringAssert.Contains(i.BuildJson, "\"target\":\"prod\"");
    }

    [TestMethod]
    public void The_repo_root_is_a_dot_and_the_default_dockerfile_is_used()
    {
        var r = BuildInputsReader.Read(Config("{\"context\":\"/home/deploy/shop/repo\"}"), Repo);
        Assert.AreEqual(".", r["api"].Inputs!.ContextRel);
        Assert.AreEqual("Dockerfile", r["api"].Inputs!.DockerfileRel);
    }

    [TestMethod]
    public void A_dockerfile_outside_the_context_but_inside_the_repo_is_still_found()
    {
        var r = BuildInputsReader.Read(Config("{\"context\":\"/home/deploy/shop/repo/api\",\"dockerfile\":\"../docker/api.Dockerfile\"}"), Repo);
        Assert.AreEqual("docker/api.Dockerfile", r["api"].Inputs!.DockerfileRel);
    }

    [DataTestMethod]
    [DataRow("{\"context\":\"/somewhere/else\"}")]
    [DataRow("{\"context\":\"https://github.com/o/r.git\"}")]
    [DataRow("{\"context\":\"/home/deploy/shop/repo/api\",\"dockerfile\":\"../../outside/Dockerfile\"}")]
    [DataRow("{\"context\":\"/home/deploy/shop/repo/we ird;rm\"}")]
    public void Anything_it_cannot_prove_is_reported_as_unsupported_never_guessed(string build)
    {
        var o = BuildInputsReader.Read(Config(build), Repo)["api"];
        Assert.IsNull(o.Inputs);
        Assert.IsFalse(string.IsNullOrWhiteSpace(o.Unsupported));
    }

    [TestMethod]
    public void The_settings_text_does_not_depend_on_the_order_things_were_written_in()
    {
        var a = BuildInputsReader.Read(Config("{\"context\":\"/home/deploy/shop/repo/api\",\"args\":{\"A\":\"1\",\"B\":\"2\"},\"target\":\"prod\"}"), Repo)["api"].Inputs!;
        var b = BuildInputsReader.Read(Config("{\"target\":\"prod\",\"args\":{\"B\":\"2\",\"A\":\"1\"},\"context\":\"/home/deploy/shop/repo/api\"}"), Repo)["api"].Inputs!;
        Assert.AreEqual(a.BuildJson, b.BuildJson);
    }

    // ── the fingerprint itself ────────────────────────────────────────────────

    private static BuildInputs Inputs(string json = "{\"target\":\"prod\"}") => new("api", "api", "api/Dockerfile", json);
    private static ProbedInputs Probed(string tree = "t1", string df = "d1", int untracked = 0) => new(tree, df, untracked);

    [TestMethod]
    public void Identical_inputs_give_identical_fingerprints()
    {
        Assert.AreEqual(BuildFingerprint.Compute(Inputs(), Probed(), "c1"), BuildFingerprint.Compute(Inputs(), Probed(), "c1"));
    }

    [TestMethod]
    public void Changing_any_input_changes_the_fingerprint()
    {
        var baseline = BuildFingerprint.Compute(Inputs(), Probed(), "c1");
        Assert.AreNotEqual(baseline, BuildFingerprint.Compute(Inputs(), Probed(tree: "t2"), "c1"), "a file in the build folder changed");
        Assert.AreNotEqual(baseline, BuildFingerprint.Compute(Inputs(), Probed(df: "d2"), "c1"), "the Dockerfile changed");
        Assert.AreNotEqual(baseline, BuildFingerprint.Compute(Inputs(), Probed(), "c2"), "the compose file changed");
        Assert.AreNotEqual(baseline, BuildFingerprint.Compute(Inputs("{\"target\":\"dev\"}"), Probed(), "c1"), "a build setting changed");
        Assert.AreNotEqual(baseline, BuildFingerprint.Compute(new BuildInputs("api", "other", "api/Dockerfile", "{\"target\":\"prod\"}"), Probed(), "c1"), "a different folder");
    }

    [TestMethod]
    public void The_count_of_stray_files_does_not_change_the_fingerprint_but_is_reported_separately()
    {
        Assert.AreEqual(BuildFingerprint.Compute(Inputs(), Probed(untracked: 0), "c1"), BuildFingerprint.Compute(Inputs(), Probed(untracked: 5), "c1"));
    }

    [TestMethod]
    public void A_missing_build_folder_gives_no_fingerprint_rather_than_a_wrong_one()
    {
        Assert.IsNull(BuildFingerprint.Compute(Inputs(), new ProbedInputs(null, "d1", 0), "c1"));
    }

    // ── the git probe ─────────────────────────────────────────────────────────

    [TestMethod]
    public void The_probe_reads_git_objects_inside_the_repo_folder_only()
    {
        var c = BuildFingerprint.ProbeCommand(Target, NewSha, [Inputs(), new BuildInputs("web", ".", "Dockerfile", "{}")], "docker-compose.yml", checkUntracked: true);
        StringAssert.Contains(c, "cd '/home/deploy/shop/repo'");
        StringAssert.Contains(c, $"'{NewSha}:api'");
        StringAssert.Contains(c, $"'{NewSha}^{{tree}}'");
        StringAssert.Contains(c, $"'{NewSha}:docker-compose.yml'");
        StringAssert.Contains(c, "status --porcelain --ignored");
    }

    [TestMethod]
    public void Stray_files_are_only_counted_when_asked_because_only_the_current_checkout_has_them()
    {
        var c = BuildFingerprint.ProbeCommand(Target, OldSha, [Inputs()], null, checkUntracked: false);
        Assert.IsFalse(c.Contains("status --porcelain"));
    }

    [DataTestMethod]
    [DataRow("a b")]
    [DataRow("a;rm -rf /")]
    [DataRow("../x")]
    [DataRow("$(id)")]
    public void The_probe_refuses_paths_that_are_not_plain(string path)
    {
        Assert.ThrowsException<ArgumentException>(() => BuildFingerprint.ProbeCommand(Target, NewSha, [new BuildInputs("api", path, "Dockerfile", "{}")], null, true));
    }

    [TestMethod]
    public void The_probe_output_is_read_back()
    {
        var p = BuildFingerprint.ParseProbe(["compose|c1", "svc|api|t1|d1|3", "svc|web|none|none|0", "noise"]);
        Assert.AreEqual("c1", p.ComposeBlob);
        Assert.AreEqual(new ProbedInputs("t1", "d1", 3), p.Services["api"]);
        Assert.IsNull(p.Services["web"].ContextTree);
    }

    private static readonly string? Bash = new[]
    {
        "/bin/bash", "/usr/bin/bash", @"C:\Program Files\Git\bin\bash.exe", @"C:\Program Files\Git\usr\bin\bash.exe",
    }.FirstOrDefault(File.Exists);

    private static (int Exit, string Out, string Err) Run(string script)
    {
        if (Bash is null) Assert.Inconclusive("No POSIX bash on this machine.");
        var psi = new ProcessStartInfo(Bash!, "-s") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi)!;
        p.StandardInput.Write(script); p.StandardInput.Close();
        var o = p.StandardOutput.ReadToEnd(); var e = p.StandardError.ReadToEnd();
        p.WaitForExit(30000);
        return (p.ExitCode, o, e);
    }

    [TestMethod]
    public void In_a_real_repo_only_a_change_inside_the_build_folder_changes_its_tree()
    {
        if (Bash is null) Assert.Inconclusive("No POSIX bash on this machine.");
        var proj = "/tmp/shipright-fp-" + Guid.NewGuid().ToString("N")[..8];
        var target = new JitTarget("h", "u", "/k", proj);
        string Probe(string sha) => BuildFingerprint.ProbeCommand(target, sha, [new BuildInputs("api", "api", "api/Dockerfile", "{}")], "docker-compose.yml", checkUntracked: false);
        // the probe is generated for a placeholder sha and the real one is swapped in
        string For(string shaVar) => Probe("abcdef1").Replace("'abcdef1:", "\"$" + shaVar + ":").Replace("'abcdef1^", "\"$" + shaVar + "^")
            .Replace("api'", "api\"").Replace("Dockerfile'", "Dockerfile\"").Replace("docker-compose.yml'", "docker-compose.yml\"");

        var script = $@"
set -e
export GIT_AUTHOR_NAME=t GIT_AUTHOR_EMAIL=t@t GIT_COMMITTER_NAME=t GIT_COMMITTER_EMAIL=t@t
rm -rf '{proj}'; mkdir -p '{proj}/repo/api' '{proj}/repo/docs'; cd '{proj}/repo'
git init -q .
printf 'FROM x' > api/Dockerfile; printf 'code1' > api/main.txt; printf 'docs1' > docs/readme.md; printf 'svc: 1' > docker-compose.yml
git add -A; git commit -qm one; S1=$(git rev-parse HEAD)
printf 'docs2' > docs/readme.md; git add -A; git commit -qm docs; S2=$(git rev-parse HEAD)
printf 'code2' > api/main.txt; git add -A; git commit -qm code; S3=$(git rev-parse HEAD)
echo ONE; {For("S1")}
echo TWO; {For("S2")}
echo THREE; {For("S3")}
rm -rf '{proj}'
";
        var (exit, o, e) = Run(script);
        Assert.AreEqual(0, exit, e + o);
        string Section(string name, string next) { var a = o.IndexOf(name, StringComparison.Ordinal) + name.Length; var b = next is null ? o.Length : o.IndexOf(next, StringComparison.Ordinal); return o[a..b]; }
        var s1 = BuildFingerprint.ParseProbe(Section("ONE", "TWO").Split('\n').Select(l => l.Trim()));
        var s2 = BuildFingerprint.ParseProbe(Section("TWO", "THREE").Split('\n').Select(l => l.Trim()));
        var s3 = BuildFingerprint.ParseProbe(Section("THREE", null!).Split('\n').Select(l => l.Trim()));
        Assert.AreEqual(s1.Services["api"].ContextTree, s2.Services["api"].ContextTree, "a docs-only change must not change the api build folder");
        Assert.AreNotEqual(s2.Services["api"].ContextTree, s3.Services["api"].ContextTree, "a code change inside the folder must");
        Assert.AreEqual(s1.Services["api"].DockerfileBlob, s3.Services["api"].DockerfileBlob);
        Assert.AreEqual(s1.ComposeBlob, s3.ComposeBlob);
    }

    // ── the shadow report ─────────────────────────────────────────────────────

    private sealed class Script
    {
        public Func<string, (int, IReadOnlyList<string>)> Handler { get; set; } = _ => (0, []);
        public List<string> Said { get; } = [];
        public Task<(int Exit, IReadOnlyList<string> Lines)> Exec(string cmd) { var (e, l) = Handler(cmd); return Task.FromResult((e, l)); }
        public Task Say(string s) { Said.Add(s); return Task.CompletedTask; }
    }

    private static Task Report(Script s, string? prevSha = OldSha, string? prevTag = "r1-1111111", TimeSpan? build = null) =>
        JitFingerprintShadow.ReportAsync(s.Exec, s.Say, Target, Config(), "docker-compose.yml",
            [("api", "shop/api")], NewSha, "r2-ab8b4a9", prevSha, prevTag, build ?? TimeSpan.FromSeconds(38.2));

    private static (int, IReadOnlyList<string>) Answer(string cmd, string newTree, string oldTree, int untracked, string newId, string oldId)
    {
        if (cmd.Contains("docker image inspect")) return (0, [cmd.Contains("r2-ab8b4a9") ? newId : oldId]);
        if (cmd.Contains($"'{NewSha}")) return (0, ["compose|c1", $"svc|api|{newTree}|d1|{untracked}"]);
        return (0, ["compose|c1", $"svc|api|{oldTree}|d1|0"]);
    }

    [TestMethod]
    public async Task Unchanged_inputs_and_an_identical_image_confirm_the_prediction()
    {
        var s = new Script { Handler = c => Answer(c, "t1", "t1", 0, "sha256:aaa", "sha256:aaa") };
        await Report(s);
        var all = string.Join("\n", s.Said);
        StringAssert.Contains(all, "api");
        StringAssert.Contains(all, "unchanged since r1-1111111");
        StringAssert.Contains(all, "prediction confirmed");
        StringAssert.Contains(all, "38.2");
    }

    [TestMethod]
    public async Task Unchanged_inputs_but_a_different_image_is_flagged_loudly_as_a_wrong_prediction()
    {
        var s = new Script { Handler = c => Answer(c, "t1", "t1", 0, "sha256:aaa", "sha256:bbb") };
        await Report(s);
        StringAssert.Contains(string.Join("\n", s.Said), "WRONG PREDICTION");
    }

    [TestMethod]
    public async Task Changed_inputs_say_a_build_was_needed()
    {
        var s = new Script { Handler = c => Answer(c, "t2", "t1", 0, "sha256:aaa", "sha256:bbb") };
        await Report(s);
        var all = string.Join("\n", s.Said);
        StringAssert.Contains(all, "changed");
        Assert.IsFalse(all.Contains("WRONG PREDICTION"));
    }

    [TestMethod]
    public async Task Stray_files_in_the_build_folder_make_the_fingerprint_unreliable_and_say_so()
    {
        var s = new Script { Handler = c => Answer(c, "t1", "t1", 4, "sha256:aaa", "sha256:aaa") };
        await Report(s);
        StringAssert.Contains(string.Join("\n", s.Said), "not reliable");
    }

    [TestMethod]
    public async Task The_first_release_has_nothing_to_compare_with()
    {
        var s = new Script();
        await Report(s, prevSha: null, prevTag: null);
        StringAssert.Contains(string.Join("\n", s.Said), "first");
    }

    [TestMethod]
    public async Task A_server_that_cannot_answer_never_breaks_the_report()
    {
        var s = new Script { Handler = _ => (1, ["error"]) };
        await Report(s);
        StringAssert.Contains(string.Join("\n", s.Said), "could not");
    }

    [TestMethod]
    public async Task A_build_folder_outside_the_repo_is_explained_and_nothing_is_run()
    {
        var calls = 0;
        var s = new Script { Handler = _ => { calls++; return (0, []); } };
        await JitFingerprintShadow.ReportAsync(s.Exec, s.Say, Target, Config("{\"context\":\"/elsewhere\"}"), null, [("api", "shop/api")], NewSha, "r2-ab8b4a9", OldSha, "r1-1111111", TimeSpan.FromSeconds(1));
        Assert.AreEqual(0, calls);
        StringAssert.Contains(string.Join("\n", s.Said), "outside");
    }

    [TestMethod]
    public async Task The_report_says_nothing_is_skipped_yet()
    {
        var s = new Script { Handler = c => Answer(c, "t1", "t1", 0, "sha256:aaa", "sha256:aaa") };
        await Report(s);
        StringAssert.Contains(string.Join("\n", s.Said), "nothing is skipped");
    }
}
