using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Builds;
using ShipRight.Modules.System.WslDisk;
using ShipRight.Shared.ProcessRunner;

namespace ShipRight.Tests.WslDisk;

[TestClass]
public class WslDiskShrinkerTests
{
    private sealed class Handler(Func<string, string[], ProcessResult> respond) : IProcessRunner
    {
        public List<string> Calls { get; } = [];

        public Task<ProcessResult> RunAsync(string executable, string[] args, string? workingDir,
            Func<string, Task>? onOutput = null, Func<string, Task>? onError = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? envOverride = null, TimeSpan? timeout = null, string? stdin = null)
        {
            Calls.Add(executable + " " + string.Join(" ", args));
            return Task.FromResult(respond(executable, args));
        }
    }

    private sealed class NoBuilds : IBuildStore
    {
        public int Count => 0;
        public Task SaveAsync(BuildRecord record) => Task.CompletedTask;
        public Task<BuildRecord?> GetByIdAsync(string id) => Task.FromResult<BuildRecord?>(null);
        public Task<List<BuildRecord>> QueryAsync(string? projectId, string? status, DateTime? from, DateTime? to,
            string? gitTag, int page, int pageSize) => Task.FromResult(new List<BuildRecord>());
        public Task<int> CountQueryAsync(string? projectId, string? status, DateTime? from, DateTime? to, string? gitTag) => Task.FromResult(0);
        public Task MarkInterruptedAsync() => Task.CompletedTask;
    }

    private static ProcessResult Ok(string o = "") => new(0, o, "", TimeSpan.Zero);
    private static ProcessResult Fail(string e = "") => new(1, "", e, TimeSpan.Zero);

    private static string SeedVhdx(string root, long size)
    {
        var dir = Path.Combine(root, "Pkg", "LocalState");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "ext4.vhdx");
        File.WriteAllBytes(path, new byte[size]);
        return path;
    }

    private static ProcessResult Wsl(string exe, string[] args) =>
        exe == "wsl" && args.Contains("--list") ? Ok("  NAME    STATE    VERSION\r\n* Ubuntu  Stopped  2\r\n") : Ok();

    private static (WslDiskShrinker Shrinker, string Root) Build(Func<string, string[], ProcessResult> respond, out Handler runner)
    {
        var root = Path.Combine(Path.GetTempPath(), "shipright-shrink-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        runner = new Handler(respond);
        return (new WslDiskShrinker(new WslDiskService(runner, new NoBuilds(), [root]), runner), root);
    }

    [TestMethod]
    public async Task WhenOptimizeVhdIsAvailable_ItIsUsed_AndDiskpartIsNotTouched()
    {
        var (shrinker, root) = Build(Wsl, out var runner);
        try
        {
            SeedVhdx(root, 4096);

            var result = await shrinker.ShrinkAsync(default, _ => { });

            Assert.IsTrue(result.Succeeded);
            Assert.IsTrue(runner.Calls.Any(c => c.Contains("Optimize-VHD -Path")));
            Assert.IsFalse(runner.Calls.Any(c => c.Contains("diskpart")));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task WithoutHyperV_FallsBackToAnElevatedDiskpart_WithTheRightScript()
    {
        string? script = null;
        string? vhdx = null;
        var (shrinker, root) = Build((exe, args) =>
        {
            if (exe == "powershell" && string.Join(" ", args).Contains("Get-Command Optimize-VHD")) return Fail();
            if (exe == "powershell" && string.Join(" ", args).Contains("diskpart"))
            {
                var path = Regex.Match(string.Join(" ", args), "/s','\"(?<p>[^\"]+)\"'").Groups["p"].Value;
                script = File.ReadAllText(path);
                File.WriteAllBytes(vhdx!, new byte[1024]);   // diskpart "compacted" the disk
                return Ok();
            }
            return Wsl(exe, args);
        }, out var runner);
        try
        {
            vhdx = SeedVhdx(root, 1_000_000);

            var result = await shrinker.ShrinkAsync(default, _ => { });

            Assert.IsTrue(result.Succeeded, string.Join(" | ", result.Messages));
            Assert.IsTrue(runner.Calls.Any(c => c.Contains("-Verb RunAs")), "diskpart needs administrator rights");
            StringAssert.Contains(script, $"select vdisk file=\"{vhdx}\"");
            StringAssert.Contains(script, "attach vdisk readonly");
            StringAssert.Contains(script, "compact vdisk");
            StringAssert.Contains(script, "detach vdisk");
            Assert.AreEqual(1_000_000, result.Files.Single().BeforeBytes);
            Assert.AreEqual(1024, result.Files.Single().AfterBytes);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task WhenTheAdminPromptIsDeclined_SaysSo_AndDoesNotClaimSuccess()
    {
        var (shrinker, root) = Build((exe, args) =>
        {
            var joined = string.Join(" ", args);
            if (exe == "powershell" && joined.Contains("Get-Command Optimize-VHD")) return Fail();
            if (exe == "powershell" && joined.Contains("diskpart")) return new ProcessResult(1, "", "The operation was canceled by the user.", TimeSpan.Zero);
            return Wsl(exe, args);
        }, out _);
        try
        {
            SeedVhdx(root, 4096);

            var result = await shrinker.ShrinkAsync(default, _ => { });

            Assert.IsFalse(result.Succeeded);
            Assert.IsTrue(result.Messages.Any(m => m.Contains("administrator")));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task WhenDiskpartRunsButNothingShrinks_IsReportedAsNotSucceeding()
    {
        var (shrinker, root) = Build((exe, args) =>
            exe == "powershell" && string.Join(" ", args).Contains("Get-Command Optimize-VHD") ? Fail() : Wsl(exe, args), out _);
        try
        {
            SeedVhdx(root, 4096);

            var result = await shrinker.ShrinkAsync(default, _ => { });

            Assert.IsFalse(result.Succeeded);
        }
        finally { Directory.Delete(root, true); }
    }

    // ── a disk the Store folder does not know about ──────────────────

    private static (WslDiskShrinker Shrinker, string Root, string Elsewhere) BuildWithLocator(
        Func<string, string[], ProcessResult> respond, Func<IEnumerable<(string, string?)>> registered, out Handler runner)
    {
        var root = Path.Combine(Path.GetTempPath(), "shipright-shrink-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var elsewhere = Path.Combine(root, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        runner = new Handler(respond);
        var shrinker = new WslDiskShrinker(new WslDiskService(runner, new NoBuilds(), [Path.Combine(root, "Packages")]), runner,
            new WslVhdxLocator(registered, []));
        return (shrinker, root, elsewhere);
    }

    [TestMethod]
    public async Task ADiskTheStoreFolderDoesNotKnow_IsFoundThroughWsl_AndShrunkWithDiskpart()
    {
        string? vhdx = null;
        string? script = null;
        var (shrinker, root, elsewhere) = BuildWithLocator((exe, args) =>
        {
            var joined = string.Join(" ", args);
            if (exe == "powershell" && joined.Contains("diskpart"))
            {
                script = File.ReadAllText(Regex.Match(joined, "/s','\"(?<p>[^\"]+)\"'").Groups["p"].Value);
                File.WriteAllBytes(vhdx!, new byte[1024]);
                return Ok();
            }
            return Wsl(exe, args);
        }, () => [], out var runner);
        try
        {
            vhdx = Path.Combine(elsewhere, "ext4.vhdx");
            File.WriteAllBytes(vhdx, new byte[1_000_000]);
            var withDisk = new WslDiskShrinker(new WslDiskService(runner, new NoBuilds(), [Path.Combine(root, "Packages")]), runner,
                new WslVhdxLocator(() => [(elsewhere, null)], []));

            var result = await withDisk.ShrinkAsync(default, _ => { });

            Assert.IsTrue(result.Succeeded, string.Join(" | ", result.Messages));
            StringAssert.Contains(script, $"select vdisk file=\"{vhdx}\"");
            Assert.AreEqual(1024, result.Files.Single().AfterBytes);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task WhenNothingIsFoundAnywhere_SaysWhereItLooked_AndPromisesNothingWasChanged()
    {
        var (shrinker, root, _) = BuildWithLocator(Wsl, () => [(@"D:\wsl\ubuntu", null)], out _);
        try
        {
            var result = await shrinker.ShrinkAsync(default, _ => { });

            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual("No WSL virtual disk found", result.BlockedReason);
            Assert.IsTrue(result.Messages.Any(m => m.Contains(@"D:\wsl\ubuntu")), string.Join(" | ", result.Messages));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task WhenTheStoreFolderFindsTheDisk_TheNewLookupIsNeverConsulted()
    {
        var root = Path.Combine(Path.GetTempPath(), "shipright-shrink-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            SeedVhdx(root, 4096);
            var runner = new Handler(Wsl);
            var shrinker = new WslDiskShrinker(new WslDiskService(runner, new NoBuilds(), [root]), runner,
                new WslVhdxLocator(() => throw new InvalidOperationException("must not be asked"), []));

            var result = await shrinker.ShrinkAsync(default, _ => { });

            Assert.IsTrue(result.Succeeded);
            Assert.IsTrue(runner.Calls.Any(c => c.Contains("Optimize-VHD -Path")), "the original path ran as before");
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task OtherBlockers_PassStraightThrough_WithoutTryingDiskpart()
    {
        // No WSL distros at all: the original service refuses, and so must the fallback.
        var (shrinker, root) = Build((exe, args) => Fail(), out var runner);
        try
        {
            var result = await shrinker.ShrinkAsync(default, _ => { });

            Assert.IsFalse(result.Succeeded);
            Assert.IsFalse(runner.Calls.Any(c => c.Contains("diskpart")));
        }
        finally { Directory.Delete(root, true); }
    }
}
