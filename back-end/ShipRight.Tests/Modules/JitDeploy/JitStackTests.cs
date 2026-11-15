using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.JitDeploy;

namespace ShipRight.Tests.Modules.JitDeploy;

/// <summary>
/// The "stack" folder beside the checkout: a tiny compose.yaml, a .env and a dc helper, so a person can run plain
/// <c>docker compose</c> commands there with no options. ShipRight never edits a file there that it did not write.
/// </summary>
[TestClass]
public class JitStackTests
{
    private static readonly JitTarget Target = new("h", "u", "/k", "/home/deploy/shop");
    private static readonly IReadOnlyList<TagBinding> Bindings = [new("API_TAG", "shop/api"), new("WEB_TAG", "shop/web")];
    private const string Tag = "r2-ab8b4a9";

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
        p.WaitForExit(30000);
        return (p.ExitCode, o, e);
    }

    // ── where it lives ────────────────────────────────────────────────────────

    [TestMethod]
    public void The_stack_folder_sits_beside_the_checkout_never_inside_it()
    {
        Assert.AreEqual("/home/deploy/shop/stack", JitStack.Dir(Target));
        Assert.IsFalse(JitStack.Dir(Target).StartsWith(Target.RepoDir + "/"));
    }

    // ── compose.yaml ──────────────────────────────────────────────────────────

    [TestMethod]
    public void The_compose_file_includes_the_real_one_in_place_with_absolute_paths()
    {
        var y = JitStack.ComposeYaml(Target, "shop", "docker-compose.yml", null);
        StringAssert.Contains(y, "Managed by ShipRight");
        StringAssert.Contains(y, "name: shop");
        StringAssert.Contains(y, "include:");
        StringAssert.Contains(y, "\"/home/deploy/shop/repo/docker-compose.yml\"");
        StringAssert.Contains(y, "project_directory: \"/home/deploy/shop/repo\"");
        StringAssert.Contains(y, "env_file: \"/home/deploy/shop/stack/.env\"");
        Assert.IsFalse(y.Contains(".deploy-state"), "no build instructions file when there are none");
    }

    [TestMethod]
    public void The_build_instructions_file_is_merged_in_when_there_is_one()
    {
        var y = JitStack.ComposeYaml(Target, "shop", "deploy/c.yml", "/home/deploy/shop/.deploy-state/compose.override.yml");
        StringAssert.Contains(y, "\"/home/deploy/shop/repo/deploy/c.yml\"");
        StringAssert.Contains(y, "\"/home/deploy/shop/.deploy-state/compose.override.yml\"");
    }

    [TestMethod]
    public void The_compose_file_never_holds_a_secret()
    {
        var y = JitStack.ComposeYaml(Target, "shop", "docker-compose.yml", null);
        Assert.IsFalse(y.Contains("PASSWORD") || y.Contains("TOKEN"));
    }

    [TestMethod]
    public void A_dangerous_project_name_or_compose_path_is_refused()
    {
        Assert.ThrowsException<ArgumentException>(() => JitStack.ComposeYaml(Target, "a b; rm", "docker-compose.yml", null));
        Assert.ThrowsException<ArgumentException>(() => JitStack.ComposeYaml(Target, "shop", "../outside.yml", null));
    }

    // ── .env ──────────────────────────────────────────────────────────────────

    [TestMethod]
    public void The_env_file_holds_the_variables_and_the_running_releases_tags()
    {
        var e = JitStack.EnvText("MARIADB_USER=app\nSITE_DOMAIN=example.com", Bindings, Tag);
        StringAssert.Contains(e, "Managed by ShipRight");
        StringAssert.Contains(e, "MARIADB_USER=app");
        StringAssert.Contains(e, "SITE_DOMAIN=example.com");
        StringAssert.Contains(e, "API_TAG=shop/api:r2-ab8b4a9");
        StringAssert.Contains(e, "WEB_TAG=shop/web:r2-ab8b4a9");
    }

    [TestMethod]
    public void The_tags_come_last_so_they_win_over_a_variable_of_the_same_name()
    {
        var e = JitStack.EnvText("API_TAG=old", Bindings, Tag);
        Assert.IsTrue(e.LastIndexOf("API_TAG=shop/api:r2-ab8b4a9", StringComparison.Ordinal) > e.IndexOf("API_TAG=old", StringComparison.Ordinal));
    }

    [TestMethod]
    public void With_no_variables_the_env_file_still_carries_the_tags()
    {
        var e = JitStack.EnvText(null, Bindings, Tag);
        StringAssert.Contains(e, "API_TAG=shop/api:r2-ab8b4a9");
    }

    // ── the dc helper ─────────────────────────────────────────────────────────

    [TestMethod]
    public void The_helper_runs_compose_exactly_as_ShipRight_does_and_passes_its_arguments_on()
    {
        var ctx = new ComposeContext(Target, "shop", "/home/deploy/shop/.deploy-state/deploy.env", "docker-compose.yml", null);
        var s = JitStack.Script(ctx, Bindings, Tag);
        StringAssert.StartsWith(s, "#!/bin/sh");
        StringAssert.Contains(s, "Managed by ShipRight");
        StringAssert.Contains(s, "cd '/home/deploy/shop/repo'");
        StringAssert.Contains(s, "API_TAG='shop/api:r2-ab8b4a9'");
        StringAssert.Contains(s, "docker compose -p 'shop'");
        StringAssert.Contains(s, "--env-file '/home/deploy/shop/.deploy-state/deploy.env'");
        StringAssert.Contains(s, "\"$@\"");
    }

    [TestMethod]
    public void The_helper_is_valid_shell()
    {
        var ctx = new ComposeContext(Target, "shop", null, null, null);
        var (exit, _, err) = Run("-n", JitStack.Script(ctx, Bindings, Tag));
        Assert.AreEqual(0, exit, err);
    }

    // ── writing safely ────────────────────────────────────────────────────────

    [TestMethod]
    public void The_write_command_is_valid_shell_and_only_touches_the_stack_folder()
    {
        var c = JitStack.WriteCommand(Target, "yaml", "env", "script");
        var (exit, _, err) = Run("-n", c);
        Assert.AreEqual(0, exit, err);
        StringAssert.StartsWith(c, ": 'stack:write'; ");
        StringAssert.Contains(c, "/home/deploy/shop/stack");
        Assert.IsFalse(c.Contains("/repo"));
    }

    [TestMethod]
    public void In_a_real_folder_files_are_written_with_the_right_modes_and_a_foreign_file_is_never_overwritten()
    {
        if (Bash is null) Assert.Inconclusive("No POSIX bash on this machine.");
        var proj = "/tmp/shipright-stack-" + Guid.NewGuid().ToString("N")[..8];
        var t = new JitTarget("h", "u", "/k", proj);
        var write = JitStack.WriteCommand(t, "# Managed by ShipRight\nyaml: 1\n", "# Managed by ShipRight\nA=1\n", "#!/bin/sh\n# Managed by ShipRight\necho hi\n");
        var script = $@"
rm -rf '{proj}'; mkdir -p '{proj}'
{write}
echo FIRST=$?
ls -l '{proj}/stack/compose.yaml' '{proj}/stack/.env' '{proj}/stack/dc' | awk '{{print $1, $NF}}'
cat '{proj}/stack/.env'
{write}
echo SECOND=$?
printf 'my own file' > '{proj}/stack/compose.yaml'
{write}
echo THIRD=$?
cat '{proj}/stack/compose.yaml'
rm -rf '{proj}'
";
        var (exit, o, e) = Run("-s", script);
        Assert.AreEqual(0, exit, e + o);
        StringAssert.Contains(o, "FIRST=0");
        StringAssert.Contains(o, "-rw------- ");            // .env is private
        StringAssert.Contains(o, "-rwx");                   // dc can be run (the group bits are not shown on Windows file systems)
        StringAssert.Contains(o, "A=1");
        StringAssert.Contains(o, "SECOND=0");               // our own files are rewritten
        StringAssert.Contains(o, "THIRD=1");                // a file we did not write is not
        StringAssert.Contains(o, "not managed by ShipRight");
        StringAssert.Contains(o, "my own file");
    }

    [TestMethod]
    public void Removing_the_shortcut_only_removes_files_that_carry_the_marker()
    {
        if (Bash is null) Assert.Inconclusive("No POSIX bash on this machine.");
        var proj = "/tmp/shipright-stack-" + Guid.NewGuid().ToString("N")[..8];
        var t = new JitTarget("h", "u", "/k", proj);
        var script = $@"
rm -rf '{proj}'; mkdir -p '{proj}/stack'
printf '# Managed by ShipRight\n' > '{proj}/stack/compose.yaml'
printf 'mine' > '{proj}/stack/.env'
printf '# Managed by ShipRight\n' > '{proj}/stack/dc'
{JitStack.RemoveShortcutCommand(t)}
[ -e '{proj}/stack/compose.yaml' ] && echo YAML=kept || echo YAML=gone
[ -e '{proj}/stack/.env' ] && echo ENV=kept || echo ENV=gone
[ -e '{proj}/stack/dc' ] && echo DC=kept || echo DC=gone
rm -rf '{proj}'
";
        var (exit, o, e) = Run("-s", script);
        Assert.AreEqual(0, exit, e + o);
        StringAssert.Contains(o, "YAML=gone");
        StringAssert.Contains(o, "ENV=kept");
        StringAssert.Contains(o, "DC=kept");
    }

    // ── other commands ────────────────────────────────────────────────────────

    [TestMethod]
    public void The_shortcut_is_rendered_from_its_own_folder_with_no_options()
    {
        var c = JitStack.ConfigViaStackCommand(Target);
        StringAssert.Contains(c, "cd '/home/deploy/shop/stack'");
        StringAssert.Contains(c, "docker compose config --format json");
        Assert.IsFalse(c.Contains(" -f ") || c.Contains("--env-file") || c.Contains(" -p "), "the whole point is that it needs none");
    }

    [TestMethod]
    public void The_compose_file_is_found_in_the_usual_order()
    {
        var c = JitStack.DetectComposeFileCommand(Target);
        Assert.IsTrue(c.IndexOf("compose.yaml", StringComparison.Ordinal) < c.IndexOf("docker-compose.yml", StringComparison.Ordinal));
        var (exit, _, err) = Run("-n", c);
        Assert.AreEqual(0, exit, err);
    }

    // ── comparing the two ways of rendering ───────────────────────────────────

    private static string Json(string api = "shop/api:r2", string web = "shop/web:r2", string network = "default") =>
        "{\"name\":\"shop\",\"services\":{\"api\":{\"image\":\"" + api + "\",\"environment\":{\"B\":\"2\",\"A\":\"1\"}},\"web\":{\"image\":\"" + web + "\"}},\"networks\":{\"" + network + "\":{}}}";

    [TestMethod]
    public void The_same_configuration_written_in_a_different_order_is_the_same()
    {
        var a = Json();
        var b = "{\"networks\":{\"default\":{}},\"services\":{\"web\":{\"image\":\"shop/web:r2\"},\"api\":{\"environment\":{\"A\":\"1\",\"B\":\"2\"},\"image\":\"shop/api:r2\"}},\"name\":\"shop\"}";
        var r = JitStack.Compare(a, b);
        Assert.IsTrue(r.Same, string.Join(",", r.Differences));
    }

    [TestMethod]
    public void A_service_that_differs_is_named_and_nothing_else_is()
    {
        var r = JitStack.Compare(Json(), Json(api: "shop/api:other"));
        Assert.IsFalse(r.Same);
        CollectionAssert.AreEqual(new[] { "api" }, r.Differences.ToArray());
    }

    [TestMethod]
    public void Settings_outside_the_services_are_reported_as_other_settings()
    {
        var r = JitStack.Compare(Json(), Json(network: "other"));
        Assert.IsFalse(r.Same);
        CollectionAssert.Contains(r.Differences.ToList(), "other settings");
    }

    [TestMethod]
    public void A_missing_or_extra_service_counts_as_a_difference()
    {
        var r = JitStack.Compare(Json(), "{\"name\":\"shop\",\"services\":{\"api\":{\"image\":\"shop/api:r2\",\"environment\":{\"B\":\"2\",\"A\":\"1\"}}},\"networks\":{\"default\":{}}}");
        Assert.IsFalse(r.Same);
        CollectionAssert.Contains(r.Differences.ToList(), "web");
    }

    [TestMethod]
    public void Output_that_is_not_json_is_never_called_the_same()
    {
        Assert.IsFalse(JitStack.Compare(Json(), "error while loading include").Same);
        Assert.IsFalse(JitStack.Compare("", "").Same);
    }

    [TestMethod]
    public void Values_are_never_part_of_the_report()
    {
        var a = "{\"services\":{\"api\":{\"environment\":{\"PW\":\"hunter2\"}}}}";
        var b = "{\"services\":{\"api\":{\"environment\":{\"PW\":\"different\"}}}}";
        var r = JitStack.Compare(a, b);
        Assert.IsFalse(string.Join(" ", r.Differences).Contains("hunter2"));
    }
}
