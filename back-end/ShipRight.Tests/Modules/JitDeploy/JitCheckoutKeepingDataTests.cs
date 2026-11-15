using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.JitDeploy;

namespace ShipRight.Tests.Modules.JitDeploy;

[TestClass]
public class JitCheckoutKeepingDataTests
{
    private static readonly JitTarget Target = new("h", "u", "/k", "/home/deploy/shop");
    private const string Sha = "ab8b4a9f3c1d2e5f6a7b8c9d0e1f2a3b4c5d6e7f";

    // ── the text of the command ───────────────────────────────────────────────

    [TestMethod]
    public void Resets_cleans_then_checks_out_in_that_order_inside_the_repo_folder()
    {
        var c = JitCommands.CheckoutKeepingData(Target, Sha);
        StringAssert.StartsWith(c, "cd '/home/deploy/shop/repo' && ");
        var iReset = c.IndexOf("reset --hard", StringComparison.Ordinal);
        var iClean = c.IndexOf("git clean", StringComparison.Ordinal);
        var iCheckout = c.IndexOf("checkout --detach", StringComparison.Ordinal);
        Assert.IsTrue(iReset > 0 && iClean > iReset && iCheckout > iClean, c);
        StringAssert.Contains(c, $"'{Sha}'");
        StringAssert.Contains(c, "submodule update --init --recursive");
    }

    [TestMethod]
    public void Never_deletes_ignored_files_because_those_are_where_runtime_data_lives()
    {
        var c = JitCommands.CheckoutKeepingData(Target, Sha);
        Assert.IsFalse(Regex.IsMatch(c, @"git clean\s+-\w*x"), "ignored files (logs, uploads, data) must be left alone: " + c);
        Assert.IsFalse(Regex.IsMatch(c, @"git clean[^&|;]*--ignored"), c);
    }

    [TestMethod]
    public void A_file_that_cannot_be_removed_is_reported_but_does_not_stop_the_deploy()
    {
        var c = JitCommands.CheckoutKeepingData(Target, Sha);
        var iClean = c.IndexOf("git clean", StringComparison.Ordinal);
        var iCheckout = c.IndexOf("checkout --detach", StringComparison.Ordinal);
        var between = c[iClean..iCheckout];
        StringAssert.Contains(between, "||");
        StringAssert.Contains(between, "could not be removed");
    }

    [TestMethod]
    public void The_original_Checkout_is_unchanged()
    {
        StringAssert.Contains(JitCommands.Checkout(Target, Sha), "git clean -fdx");
    }

    // ── what it really does, in a real git repo ───────────────────────────────

    private static readonly string? Bash = new[]
    {
        "/bin/bash", "/usr/bin/bash", @"C:\Program Files\Git\bin\bash.exe", @"C:\Program Files\Git\usr\bin\bash.exe",
    }.FirstOrDefault(File.Exists);

    private static (int Exit, string Out, string Err) Run(string script)
    {
        if (Bash is null) Assert.Inconclusive("No POSIX bash on this machine.");
        var psi = new ProcessStartInfo(Bash!, "-s")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        p.StandardInput.Write(script);
        p.StandardInput.Close();
        var o = p.StandardOutput.ReadToEnd();
        var e = p.StandardError.ReadToEnd();
        p.WaitForExit(30000);
        return (p.ExitCode, o, e);
    }

    [TestMethod]
    public void In_a_real_repo_tracked_changes_and_stray_files_go_but_ignored_data_stays()
    {
        if (Bash is null) Assert.Inconclusive("No POSIX bash on this machine.");
        var (_, home, _) = Run("echo \"$HOME\"");
        var projDir = "/tmp/shipright-ckdata-" + Guid.NewGuid().ToString("N")[..8];
        var target = new JitTarget("h", "u", "/k", projDir);
        var cmd = JitCommands.CheckoutKeepingData(target, "abcdef1");

        var script = $@"
set -e
export GIT_AUTHOR_NAME=t GIT_AUTHOR_EMAIL=t@t GIT_COMMITTER_NAME=t GIT_COMMITTER_EMAIL=t@t
rm -rf '{projDir}'; mkdir -p '{projDir}/repo'; cd '{projDir}/repo'
git init -q .
printf 'photos/\n' > .gitignore
printf 'v1' > app.txt
git add -A; git commit -qm one
SHA=$(git rev-parse HEAD)
printf 'dirty' > app.txt
printf 'stray' > stray.txt
mkdir -p photos; printf 'precious' > photos/a.jpg
{cmd.Replace("'abcdef1'", "\"$SHA\"")}
echo APP=$(cat app.txt)
[ -e stray.txt ] && echo STRAY=kept || echo STRAY=gone
[ -e photos/a.jpg ] && echo PHOTO=kept || echo PHOTO=gone
rm -rf '{projDir}'
";
        var (exit, o, e) = Run(script);
        Assert.AreEqual(0, exit, e + o);
        StringAssert.Contains(o, "APP=v1");
        StringAssert.Contains(o, "STRAY=gone");
        StringAssert.Contains(o, "PHOTO=kept");
    }
}
