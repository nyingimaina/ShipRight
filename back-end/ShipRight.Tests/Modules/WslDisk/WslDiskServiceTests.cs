using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Builds;
using ShipRight.Modules.System.WslDisk;
using ShipRight.Shared.ProcessRunner;

namespace ShipRight.Tests.WslDisk;

[TestClass]
public class WslDiskServiceTests
{
    private static string SeedVhdx(string root, string pkgDir, string fileName = "ext4.vhdx", long sizeBytes = 4096)
    {
        var localState = Path.Combine(root, pkgDir, "LocalState");
        Directory.CreateDirectory(localState);
        var path = Path.Combine(localState, fileName);
        File.WriteAllBytes(path, new byte[sizeBytes]);
        return path;
    }

    private sealed class FakeRunner : IProcessRunner
    {
        private readonly List<(string[] Tokens, ProcessResult Result)> _responses;
        private readonly ProcessResult _fallback;
        public List<(string[] Args, ProcessResult Result)> Calls { get; } = new();

        public FakeRunner(ProcessResult? fallback = null, params (string[] Tokens, ProcessResult Result)[] responses)
        {
            _responses = responses.ToList();
            _fallback = fallback ?? new ProcessResult(0, "", "", TimeSpan.Zero);
        }

        public Task<ProcessResult> RunAsync(string executable, string[] args, string? workingDir,
            Func<string, Task>? onOutput = null, Func<string, Task>? onError = null,
            CancellationToken ct = default, IReadOnlyDictionary<string, string>? envOverride = null,
            TimeSpan? timeout = null, string? stdin = null)
        {
            var fullArgs = new[] { executable }.Concat(args).ToArray();
            var idx = _responses.FindIndex(r => r.Tokens.All(t => fullArgs.Contains(t)));
            var result = idx >= 0 ? _responses[idx].Result : _fallback;
            if (idx >= 0) _responses.RemoveAt(idx);
            Calls.Add((args, result));
            return Task.FromResult(result);
        }
    }

    private sealed class FakeBuildStore : IBuildStore
    {
        public List<BuildRecord> Records { get; } = new();

        public int Count => Records.Count;
        public Task SaveAsync(BuildRecord record) { Records.Add(record); return Task.CompletedTask; }
        public Task<BuildRecord?> GetByIdAsync(string id) => Task.FromResult(Records.FirstOrDefault(r => r.Id == id));

        public Task<List<BuildRecord>> QueryAsync(string? projectId, string? status, DateTime? from, DateTime? to,
            string? gitTag, int page, int pageSize)
        {
            var all = status == null ? Records : Records.Where(r => r.Status.ToString() == status);
            return Task.FromResult(all.Skip((page - 1) * pageSize).Take(pageSize).ToList());
        }

        public Task<int> CountQueryAsync(string? projectId, string? status, DateTime? from, DateTime? to, string? gitTag)
            => Task.FromResult(Records.Count);

        public Task MarkInterruptedAsync() => Task.CompletedTask;
    }

    private const string DistroListVerbose =
        "  NAME      STATE           VERSION\r\n" +
        "* Ubuntu    Stopped         2\r\n" +
        "  kali      Stopped         2\r\n";

    private static WslDiskService NewService(IProcessRunner runner, IBuildStore store, string root)
        => new(runner, store, new[] { root });

    [TestMethod]
    public void GetReportAsync_NoWslNoVhdx_ReportsError()
    {
        var runner = new FakeRunner(new ProcessResult(1, "", "wsl not found", TimeSpan.Zero));
        var service = NewService(runner, new FakeBuildStore(), @"Z:\missing");

        var report = service.GetReportAsync(CancellationToken.None).GetAwaiter().GetResult();

        Assert.IsNotNull(report.Error);
        Assert.AreEqual(0, report.VhdxFiles.Count);
    }

    [TestMethod]
    public void GetReportAsync_FindsVhdxAndParsesDistros()
    {
        var root = Path.Combine(Path.GetTempPath(), "wsl-disk-test-" + Guid.NewGuid());
        try
        {
            var vhdx = SeedVhdx(root, "Ubuntu-pkg", sizeBytes: 1_048_576);
            SeedVhdx(root, "Kali-pkg", "ext4.1.vhdx", sizeBytes: 2048);
            var runner = new FakeRunner(new ProcessResult(0, DistroListVerbose, "", TimeSpan.Zero));
            var service = NewService(runner, new FakeBuildStore(), root);

            var report = service.GetReportAsync(CancellationToken.None).GetAwaiter().GetResult();

            Assert.IsNull(report.Error);
            Assert.AreEqual(2, report.VhdxFiles.Count);
            Assert.AreEqual(1_048_576, report.VhdxFiles.First(f => f.Path == vhdx).SizeBytes);
            CollectionAssert.AreEqual(new[] { "Ubuntu", "kali" }, report.Distros.Select(d => d.Name).ToArray());
            Assert.IsTrue(runner.Calls.Any(c => c.Args.SequenceEqual(new[] { "--list", "--verbose" })));
            CollectionAssert.DoesNotContain(runner.Calls.SelectMany(c => c.Args).ToArray(), "shutdown");
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void CompactAsync_ActiveRunningBuild_Blocks()
    {
        var store = new FakeBuildStore();
        store.Records.Add(new BuildRecord { Status = BuildStatus.Running });
        var service = NewService(new FakeRunner(), store, @"Z:\missing");

        var result = service.CompactAsync(CancellationToken.None, _ => { }).GetAwaiter().GetResult();

        Assert.IsFalse(result.Succeeded);
        Assert.IsTrue(result.BlockedReason!.Contains("Running"));
    }

    [TestMethod]
    public void CompactAsync_ActivePausedBuild_Blocks()
    {
        var store = new FakeBuildStore();
        store.Records.Add(new BuildRecord { Status = BuildStatus.Paused });
        var service = NewService(new FakeRunner(), store, @"Z:\missing");

        var result = service.CompactAsync(CancellationToken.None, _ => { }).GetAwaiter().GetResult();

        Assert.IsFalse(result.Succeeded);
        Assert.IsTrue(result.BlockedReason!.Contains("Paused"));
    }

    [TestMethod]
    public void CompactAsync_NoDistros_ReturnsBlocked()
    {
        var service = NewService(new FakeRunner(new ProcessResult(0, "", "", TimeSpan.Zero)), new FakeBuildStore(), @"Z:\missing");

        var result = service.CompactAsync(CancellationToken.None, _ => { }).GetAwaiter().GetResult();

        Assert.IsFalse(result.Succeeded);
        Assert.IsTrue(result.BlockedReason!.Contains("distros"));
    }

    [TestMethod]
    public void CompactAsync_NoVhdx_ReturnsBlocked()
    {
        var root = Path.Combine(Path.GetTempPath(), "wsl-disk-test-" + Guid.NewGuid());
        try
        {
            Directory.CreateDirectory(root);
            var runner = new FakeRunner(
                new ProcessResult(0, DistroListVerbose, "", TimeSpan.Zero),
                (new[] { "--shutdown" }, new ProcessResult(0, "", "", TimeSpan.Zero)));
            var service = NewService(runner, new FakeBuildStore(), root);

            var result = service.CompactAsync(CancellationToken.None, _ => { }).GetAwaiter().GetResult();

            Assert.IsFalse(result.Succeeded);
            Assert.IsTrue(result.BlockedReason!.Contains("virtual disk"));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void CompactAsync_OptimizeVhdUnavailable_ReturnsBlockedWithGuidance()
    {
        var root = Path.Combine(Path.GetTempPath(), "wsl-disk-test-" + Guid.NewGuid());
        try
        {
            SeedVhdx(root, "Ubuntu-pkg");
            var runner = new FakeRunner(
                new ProcessResult(0, DistroListVerbose, "", TimeSpan.Zero),
                (new[] { "--shutdown" }, new ProcessResult(0, "", "", TimeSpan.Zero)),
                (new[] { "powershell" }, new ProcessResult(1, "", "Optimize-VHD not recognized", TimeSpan.Zero)));
            var service = NewService(runner, new FakeBuildStore(), root);

            var result = service.CompactAsync(CancellationToken.None, _ => { }).GetAwaiter().GetResult();

            Assert.IsFalse(result.Succeeded);
            Assert.IsTrue(result.BlockedReason!.Contains("Optimize-VHD"));
            Assert.IsTrue(result.Messages.Any(m => m.Contains("Administrator")));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void CompactAsync_HappyPath_ShutsDownWslAndOptimizesEachVhdx()
    {
        var root = Path.Combine(Path.GetTempPath(), "wsl-disk-test-" + Guid.NewGuid());
        try
        {
            var vhdx1 = SeedVhdx(root, "Ubuntu-pkg", sizeBytes: 2048);
            var vhdx2 = SeedVhdx(root, "Kali-pkg", sizeBytes: 4096);
            var runner = new FakeRunner(
                new ProcessResult(0, DistroListVerbose, "", TimeSpan.Zero),
                (new[] { "--shutdown" }, new ProcessResult(0, "", "", TimeSpan.Zero)),
                (new[] { "powershell" }, new ProcessResult(0, "", "", TimeSpan.Zero)),
                (new[] { "powershell" }, new ProcessResult(0, "", "", TimeSpan.Zero)));
            var logs = new List<string>();
            var service = NewService(runner, new FakeBuildStore(), root);

            var result = service.CompactAsync(CancellationToken.None, logs.Add).GetAwaiter().GetResult();

            Assert.IsTrue(result.Succeeded);
            Assert.IsNull(result.BlockedReason);
            Assert.AreEqual(2, result.Files.Count);
            Assert.IsTrue(runner.Calls.Any(c => c.Args.SequenceEqual(new[] { "--shutdown" })));
            var optimizeCalls = runner.Calls.Where(c => c.Args.Any(a => a.Contains("Optimize-VHD -Path"))).ToList();
            Assert.AreEqual(2, optimizeCalls.Count);
            Assert.IsTrue(optimizeCalls.Any(a => a.Args.Any(x => x.Contains(vhdx1))));
            Assert.IsTrue(optimizeCalls.Any(a => a.Args.Any(x => x.Contains(vhdx2))));
            Assert.IsTrue(result.Messages.Any(m => m.Contains("Compacted")));
        }
        finally { Directory.Delete(root, true); }
    }
}