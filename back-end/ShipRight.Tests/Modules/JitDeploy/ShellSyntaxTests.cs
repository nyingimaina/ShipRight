using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.JitDeploy;

namespace ShipRight.Tests.Modules.JitDeploy;

/// <summary>
/// String assertions cannot prove a command is valid shell. These hand every command the feature can build to a real
/// POSIX bash, parse-only (<c>bash -n</c>, nothing is executed), and round-trip hostile values through
/// <see cref="ShellQuote"/>. They are skipped (inconclusive) on a machine with no bash.
/// </summary>
[TestClass]
public class ShellSyntaxTests
{
    private static readonly string? Bash = FindBash();

    private static string? FindBash()
    {
        string[] candidates =
        [
            "/bin/bash", "/usr/bin/bash",
            @"C:\Program Files\Git\bin\bash.exe", @"C:\Program Files\Git\usr\bin\bash.exe",
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    private static (int Exit, string Out, string Err) RunBash(string arguments, string stdin)
    {
        if (Bash is null) Assert.Inconclusive("No POSIX bash on this machine.");
        var psi = new ProcessStartInfo(Bash!, arguments)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        p.StandardInput.Write(stdin);
        p.StandardInput.Close();
        var o = p.StandardOutput.ReadToEnd();
        var e = p.StandardError.ReadToEnd();
        p.WaitForExit(15000);
        return (p.ExitCode, o, e);
    }

    private static void AssertParses(string name, string command)
    {
        var (exit, _, err) = RunBash("-n", command);
        Assert.AreEqual(0, exit, $"{name} is not valid shell: {err}\n{command}");
    }

    private static readonly JitTarget Target = new("h", "u", "/k", "/home/deploy/my shop");
    private static readonly ComposeContext Ctx = new(Target, "shop", "/home/deploy/my shop/.env");
    private static readonly IReadOnlyList<TagBinding> Bindings = [new("API_TAG", "shop/api"), new("WEB_TAG", "reg.local:5000/shop/web")];
    private const string Tag = "r42-ab8b4a9";

    [TestMethod]
    public void EveryCommandTheFeatureCanBuild_IsValidShell()
    {
        var commands = new Dictionary<string, string>
        {
            ["CheckDocker"] = JitCommands.CheckDocker(),
            ["CheckDisk"] = JitCommands.CheckDisk(Target, 3000),
            ["CheckGit"] = JitCommands.CheckGit("git@github.com:o/r.git"),
            ["EnsureImagesExist"] = JitCommands.EnsureImagesExist(["shop/api:r41-aaaaaaa", "shop/web:r41-aaaaaaa"]),
            ["SyncRepo"] = JitCommands.SyncRepo(Target, "https://github.com/o/r.git"),
            ["ResolveBranch"] = JitCommands.ResolveBranch(Target, "release/1.2"),
            ["ResolveCommit"] = JitCommands.ResolveCommit(Target, "ab8b4a9"),
            ["Checkout"] = JitCommands.Checkout(Target, "ab8b4a9f3c1d2e5f6a7b8c9d0e1f2a3b4c5d6e7f"),
            ["ComposeConfig"] = JitCommands.ComposeConfig(Ctx, Bindings, Tag),
            ["Build"] = JitCommands.Build(Ctx, ["api", "web"], Bindings, Tag, niceLevel: 10),
            ["StopService"] = JitCommands.StopService(Ctx, "web", 30, Bindings, Tag),
            ["StartService"] = JitCommands.StartService(Ctx, "api", Bindings, Tag),
            ["ProbeService"] = JitCommands.ProbeService(Ctx, "api", Bindings, Tag),
            ["ListImages"] = JitCommands.ListImages(),
            ["ListContainerImages"] = JitCommands.ListContainerImages(),
            ["RemoveImages"] = JitCommands.RemoveImages(["shop/api:r1-aaaaaaa", "shop/web:r1-aaaaaaa"]),
            ["StateRead"] = ReleaseStateStore.BuildReadCommand(Target),
            ["StateWrite"] = ReleaseStateStore.BuildWriteCommand(Target, ReleaseState.Empty),
        };
        foreach (var (name, cmd) in commands) AssertParses(name, cmd);
    }

    [TestMethod]
    public void CommandsWithAnAwkwardDirectory_StillParse()
    {
        var t = new JitTarget("h", "u", "/k", "/srv/o'brien & sons/$HOME");
        AssertParses("CheckDisk", JitCommands.CheckDisk(t, 1000));
        AssertParses("Checkout", JitCommands.Checkout(t, "ab8b4a9"));
        AssertParses("StateWrite", ReleaseStateStore.BuildWriteCommand(t, ReleaseState.Empty));
    }

    [DataTestMethod]
    [DataRow("plain")]
    [DataRow("with space")]
    [DataRow("it's")]
    [DataRow("''")]
    [DataRow("$(echo pwned)")]
    [DataRow("`echo pwned`")]
    [DataRow("a;echo pwned")]
    [DataRow("a && echo pwned")]
    [DataRow("$HOME ${HOME} \\n \\\\")]
    [DataRow("line1\nline2")]
    [DataRow("*?[a-z]~")]
    [DataRow("\"double\"")]
    public void Quote_RoundTripsThroughARealShell_Unchanged(string raw)
    {
        var (exit, output, err) = RunBash("-s", $"printf %s {ShellQuote.Single(raw)}");
        Assert.AreEqual(0, exit, err);
        Assert.AreEqual(raw, output);
    }

    [TestMethod]
    public void TheStateWriteCommand_ReallyWritesTheFileAtomically_AndTheReadCommandReadsItBack()
    {
        if (Bash is null) Assert.Inconclusive("No POSIX bash on this machine.");
        var dir = Path.Combine(Path.GetTempPath(), "jit-state-" + Guid.NewGuid().ToString("N")[..8]);
        var posix = dir.Replace('\\', '/');
        if (posix.Length > 1 && posix[1] == ':') posix = "/" + char.ToLowerInvariant(posix[0]) + posix[2..];   // C:/x -> /c/x for Git Bash
        try
        {
            var target = new JitTarget("h", "u", "/k", posix);
            var state = ReleaseState.Empty with { CurrentTag = "r2-bbbbbbb", PreviousTag = "r1-aaaaaaa" };

            var (readEmptyExit, emptyOut, _) = RunBash("-s", ReleaseStateStore.BuildReadCommand(target));
            Assert.AreEqual(0, readEmptyExit);
            Assert.AreEqual(ReleaseStateStore.NoStateSentinel, emptyOut.Trim());

            var (writeExit, _, writeErr) = RunBash("-s", ReleaseStateStore.BuildWriteCommand(target, state));
            Assert.AreEqual(0, writeExit, writeErr);

            var (readExit, json, _) = RunBash("-s", ReleaseStateStore.BuildReadCommand(target));
            Assert.AreEqual(0, readExit);
            var back = ReleaseStateJson.Deserialize(json);
            Assert.AreEqual("r2-bbbbbbb", back.CurrentTag);
            Assert.AreEqual("r1-aaaaaaa", back.PreviousTag);
            Assert.IsFalse(File.Exists(Path.Combine(dir, ".deploy-state", "state.json.tmp")), "the temp file is renamed away");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [TestMethod]
    public void TheDiskCheck_RunsForReal_ReportsFreeSpace_AndRefusesAnImpossibleMinimum()
    {
        if (Bash is null) Assert.Inconclusive("No POSIX bash on this machine.");
        var dir = Path.Combine(Path.GetTempPath(), "jit-disk-" + Guid.NewGuid().ToString("N")[..8]);
        var posix = dir.Replace('\\', '/');
        if (posix.Length > 1 && posix[1] == ':') posix = "/" + char.ToLowerInvariant(posix[0]) + posix[2..];
        try
        {
            var target = new JitTarget("h", "u", "/k", posix);
            var (okExit, okOut, okErr) = RunBash("-s", JitCommands.CheckDisk(target, 1));
            Assert.AreEqual(0, okExit, okErr);
            StringAssert.Contains(okOut, "Free disk space");

            var (badExit, badOut, _) = RunBash("-s", JitCommands.CheckDisk(target, int.MaxValue));
            Assert.AreEqual(1, badExit);
            StringAssert.Contains(badOut, "Not enough free disk space");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
