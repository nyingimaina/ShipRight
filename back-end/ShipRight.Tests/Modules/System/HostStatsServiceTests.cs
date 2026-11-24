using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.System;

namespace ShipRight.Tests.Modules.HostStats;

[TestClass]
public class HostStatsServiceTests
{
    [TestMethod]
    public async Task Snapshot_CalculatesPercentagesFromTheInjectedReaders()
    {
        var service = new HostStatsService(
            disk: () => (75, 100),
            memory: () => (80, 200),
            processCount: () => 12,
            processCpu: () => TimeSpan.Zero,
            clock: () => new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var stats = await service.GetAsync();

        Assert.IsTrue(stats.Available);
        Assert.AreEqual(75, stats.DiskUsedPercent);
        Assert.AreEqual(40, stats.RamUsedPercent);
        Assert.AreEqual(12, stats.ProcessCount);
        Assert.AreEqual(0, stats.ProcessCpuPercent, "the first sample has no delta yet");
    }

    [TestMethod]
    public async Task Snapshot_ClampsPercentagesAndTreatsZeroTotalsAsEmpty()
    {
        var service = new HostStatsService(
            disk: () => (300, 100),
            memory: () => (10, 0),
            processCount: () => 0,
            processCpu: () => TimeSpan.Zero,
            clock: () => new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var stats = await service.GetAsync();

        Assert.AreEqual(100, stats.DiskUsedPercent);
        Assert.AreEqual(0, stats.RamUsedPercent);
    }

    [TestMethod]
    public async Task Snapshot_UsesCpuDeltaAcrossAllCores()
    {
        var times = new Queue<DateTime>([
            new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new(2026, 1, 1, 0, 0, 10, DateTimeKind.Utc),
        ]);
        var cpu = new Queue<TimeSpan>([TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15)]);
        var service = new HostStatsService(
            disk: () => (1, 2), memory: () => (1, 2), processCount: () => 1,
            processCpu: () => cpu.Dequeue(), clock: () => times.Dequeue());

        await service.GetAsync();
        var stats = await service.GetAsync();

        var expected = 10.0 / (10 * Environment.ProcessorCount) * 100;
        Assert.AreEqual(expected, stats.ProcessCpuPercent, 0.001);
    }

    [TestMethod]
    public async Task Snapshot_UnsupportedMemory_IsReportedWithoutThrowing()
    {
        var service = new HostStatsService(
            disk: () => (1, 2),
            memory: () => throw new PlatformNotSupportedException("no memory reader"),
            processCount: () => 1,
            processCpu: () => TimeSpan.Zero,
            clock: () => new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var stats = await service.GetAsync();

        Assert.IsFalse(stats.Available);
        StringAssert.Contains(stats.Error, "Memory");
        Assert.AreEqual(50, stats.DiskUsedPercent, "the metrics that worked are still returned");
    }

    [TestMethod]
    public async Task Snapshot_FailingDisk_IsReportedWithoutThrowing()
    {
        var service = new HostStatsService(
            disk: () => throw new ArgumentException("empty drive name"),
            memory: () => (80, 200),
            processCount: () => 3,
            processCpu: () => TimeSpan.Zero,
            clock: () => new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var stats = await service.GetAsync();

        Assert.IsFalse(stats.Available);
        StringAssert.Contains(stats.Error, "Disk");
        Assert.AreEqual(40, stats.RamUsedPercent, "the metrics that worked are still returned");
    }

    [TestMethod]
    public void DiskMount_IsNeverEmpty_SoLinuxDoesNotConstructAnInvalidDriveInfo()
    {
        // Environment.SystemDirectory is empty on Linux; the old code fed that empty string to DriveInfo and the
        // whole stats endpoint returned 500, which showed as dashes in the header.
        var mount = HostStatsService.ResolveDiskMount();

        Assert.IsFalse(string.IsNullOrWhiteSpace(mount));
    }
}
