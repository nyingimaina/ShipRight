using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Database;

namespace ShipRight.Tests.Modules.Database;

[TestClass]
public class DatabaseProgressTests
{
    // ── sizes ─────────────────────────────────────────────────────────────────

    [DataTestMethod]
    [DataRow(900L, "900 bytes")]
    [DataRow(2048L, "2.0 KB")]
    [DataRow(13L * 1024 * 1024, "13.0 MB")]
    [DataRow(5L * 1024 * 1024 * 1024 + 512L * 1024 * 1024, "5.5 GB")]
    public void Sizes_are_said_the_way_a_person_would_say_them(long bytes, string expected)
    {
        Assert.AreEqual(expected, ByteSize.Format(bytes));
    }

    // ── the reporter ──────────────────────────────────────────────────────────

    private sealed class Clock { public DateTime Now = new(2026, 11, 15, 10, 0, 0, DateTimeKind.Utc); public DateTime Get() => Now; }

    private sealed record Emitted(string Stage, string Message, int Percent, long Done, long Total);

    private static (ProgressReporter R, List<Emitted> Out, Clock C) Make(long total, string stage = "upload")
    {
        var clock = new Clock();
        var list = new List<Emitted>();
        return (new ProgressReporter(stage, total, clock.Get, (s, m, p, d, t) => list.Add(new(s, m, p, d, t))), list, clock);
    }

    [TestMethod]
    public void It_speaks_in_steps_not_for_every_chunk()
    {
        var (r, o, c) = Make(1000);
        for (var b = 10; b <= 1000; b += 10) { c.Now = c.Now.AddMilliseconds(100); r.Report(b); }
        Assert.IsTrue(o.Count <= 25, $"{o.Count} messages for 100 chunks is too chatty");
        Assert.IsTrue(o.Count >= 8, "but it should still show movement");
        CollectionAssert.AreEqual(o.Select(e => e.Percent).OrderBy(x => x).ToList(), o.Select(e => e.Percent).ToList(), "percent only goes up");
    }

    [TestMethod]
    public void It_always_reports_the_end()
    {
        var (r, o, _) = Make(1000);
        r.Report(500);
        r.Report(1000);
        Assert.AreEqual(100, o.Last().Percent);
        Assert.AreEqual(1000, o.Last().Done);
    }

    [TestMethod]
    public void It_never_reports_the_same_percent_twice()
    {
        var (r, o, c) = Make(1000);
        r.Report(100); c.Now = c.Now.AddSeconds(5); r.Report(101); c.Now = c.Now.AddSeconds(5); r.Report(102);
        Assert.AreEqual(o.Select(e => e.Percent).Distinct().Count(), o.Count);
    }

    [TestMethod]
    public void A_slow_upload_still_reports_every_few_seconds()
    {
        var (r, o, c) = Make(1_000_000);
        r.Report(1000);                         // 0%
        var before = o.Count;
        c.Now = c.Now.AddSeconds(3);
        r.Report(11_000);                       // 1%: under the step, but it has been a while
        Assert.IsTrue(o.Count > before);
    }

    [TestMethod]
    public void The_upload_message_names_the_amount_and_the_speed()
    {
        var (r, o, c) = Make(13L * 1024 * 1024);
        c.Now = c.Now.AddSeconds(2);
        r.Report(5L * 1024 * 1024);
        var m = o.Last().Message;
        StringAssert.StartsWith(m, "Uploading…");
        StringAssert.Contains(m, "5.0 MB of 13.0 MB");
        StringAssert.Contains(m, "2.5 MB/s");
        StringAssert.Contains(m, "38%");
        Assert.AreEqual("upload", o.Last().Stage);
    }

    [TestMethod]
    public void The_run_message_says_how_much_of_the_script_has_been_read()
    {
        var (r, o, c) = Make(13L * 1024 * 1024, "run");
        c.Now = c.Now.AddSeconds(2);
        r.Report(6L * 1024 * 1024);
        var m = o.Last().Message;
        StringAssert.StartsWith(m, "Running…");
        StringAssert.Contains(m, "6.0 MB of 13.0 MB");
        Assert.AreEqual("run", o.Last().Stage);
    }

    [TestMethod]
    public void An_empty_file_is_simply_done()
    {
        var (r, o, _) = Make(0);
        r.Report(0);
        Assert.AreEqual(100, o.Last().Percent);
    }

    [TestMethod]
    public void More_bytes_than_expected_never_shows_over_100_percent()
    {
        var (r, o, _) = Make(100);
        r.Report(150);
        Assert.AreEqual(100, o.Last().Percent);
    }

    // ── watching a running script ─────────────────────────────────────────────

    [TestMethod]
    public void A_progress_line_from_the_server_is_read_back()
    {
        Assert.IsTrue(QueryProgress.TryParse("[progress] 524288 13631488", out var pos, out var total));
        Assert.AreEqual(524288, pos);
        Assert.AreEqual(13631488, total);
    }

    [DataTestMethod]
    [DataRow("ERROR 1064 (42000) at line 3")]
    [DataRow("[progress] abc 100")]
    [DataRow("[progress] 100")]
    [DataRow("")]
    public void Anything_else_is_left_for_the_log(string line)
    {
        Assert.IsFalse(QueryProgress.TryParse(line, out _, out _));
    }

    [TestMethod]
    public void A_command_that_reads_the_file_on_stdin_is_wrapped_so_its_position_is_watched()
    {
        var cmd = "docker exec -i db sh -c 'exec mariadb -uroot x' < /tmp/shipright-query-abc.sql";
        var w = QueryProgress.Wrap(cmd, "/tmp/shipright-query-abc.sql");
        StringAssert.Contains(w, cmd + " & p=$!");
        StringAssert.Contains(w, "/proc/$p/fdinfo/0");
        StringAssert.Contains(w, "[progress]");
        StringAssert.Contains(w, "wait $p");
    }

    [TestMethod]
    public void A_command_that_does_not_read_the_file_that_way_is_left_exactly_as_it_was()
    {
        var cmd = "docker exec db sqlcmd -i /tmp/x.sql";
        Assert.AreEqual(cmd, QueryProgress.Wrap(cmd, "/tmp/x.sql"));
    }

    [TestMethod]
    public void A_path_that_is_not_plain_is_refused()
    {
        Assert.ThrowsException<ArgumentException>(() => QueryProgress.Wrap("x < /tmp/a b.sql", "/tmp/a b.sql"));
        Assert.ThrowsException<ArgumentException>(() => QueryProgress.Wrap("x < /tmp/$(id).sql", "/tmp/$(id).sql"));
    }

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

    [TestMethod]
    public void The_wrapper_is_valid_shell()
    {
        var w = QueryProgress.Wrap("sh -c 'cat > /dev/null' < /tmp/q.sql", "/tmp/q.sql");
        var (exit, _, err) = Run("-n", w);
        Assert.AreEqual(0, exit, err);
    }

    [TestMethod]
    public void The_wrapped_command_still_succeeds_fails_and_prints_like_the_original()
    {
        if (Bash is null) Assert.Inconclusive("No POSIX bash on this machine.");
        var f = "/tmp/shipright-prog-" + Guid.NewGuid().ToString("N")[..8] + ".sql";
        var okCmd = QueryProgress.Wrap($"sh -c 'cat; echo done' < {f}", f);
        var failCmd = QueryProgress.Wrap($"sh -c 'cat >/dev/null; echo oops >&2; exit 3' < {f}", f);
        var script = $@"
printf 'select 1;\nselect 2;\n' > {f}
{okCmd}
echo OK_EXIT=$?
{failCmd}
echo FAIL_EXIT=$?
rm -f {f}
";
        var (exit, o, e) = Run("-s", script);
        Assert.AreEqual(0, exit, e);
        StringAssert.Contains(o, "select 1;");
        StringAssert.Contains(o, "done");
        StringAssert.Contains(o, "OK_EXIT=0");
        StringAssert.Contains(o, "FAIL_EXIT=3");       // the script's own exit status is what comes back
        StringAssert.Contains(e, "oops");
    }
}
