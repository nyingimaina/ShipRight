using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.RemoteHost;

namespace ShipRight.Tests.Modules.RemoteHost;

[TestClass]
public class LinuxSshMonitoringProviderTests
{
    // ── ParseCpu ──────────────────────────────────────────────────────────────

    [TestMethod]
    public void ParseCpu_GivenTwoSamples_ReturnsPercentBetween0And100()
    {
        // user=100, nice=0, system=50, idle=850 → busy=150, total=1000
        // Δbusy=150, Δtotal=1000 → 15%
        var result = LinuxSshMonitoringProvider.ParseCpu("0 0 0 0", "150 0 0 850");
        Assert.AreEqual(15.0, result, 0.1);
    }

    [TestMethod]
    public void ParseCpu_WhenIdleIs100Percent_Returns0()
    {
        var result = LinuxSshMonitoringProvider.ParseCpu("0 0 0 1000", "0 0 0 2000");
        Assert.AreEqual(0.0, result);
    }

    [TestMethod]
    public void ParseCpu_WhenNoDelta_Returns0()
    {
        var result = LinuxSshMonitoringProvider.ParseCpu("100 0 50 850", "100 0 50 850");
        Assert.AreEqual(0.0, result);
    }

    // ── ParseMemInfo ─────────────────────────────────────────────────────────

    [TestMethod]
    public void ParseMemInfo_ReturnsCorrectUsedAndTotalMb()
    {
        var lines = new[]
        {
            "MemTotal: 8192000",
            "MemAvailable: 4096000",
            "SwapTotal: 2048000",
            "SwapFree: 2048000",
        };
        var (usedMb, totalMb, swapUsedMb, swapTotalMb) = LinuxSshMonitoringProvider.ParseMemInfo(lines);
        Assert.AreEqual(8000, totalMb);
        Assert.AreEqual(4000, usedMb);
    }

    [TestMethod]
    public void ParseMemInfo_ReturnsSwapValues()
    {
        var lines = new[]
        {
            "MemTotal: 8192000",
            "MemAvailable: 6000000",
            "SwapTotal: 2048000",
            "SwapFree: 1024000",
        };
        var (_, _, swapUsedMb, swapTotalMb) = LinuxSshMonitoringProvider.ParseMemInfo(lines);
        Assert.AreEqual(2000, swapTotalMb);
        Assert.AreEqual(1000, swapUsedMb);
    }

    // ── ParseDisk ────────────────────────────────────────────────────────────

    [TestMethod]
    public void ParseDisk_ReturnsAllNonTmpfsMounts()
    {
        var lines = new[] { "/ 50000 30000", "/data 100000 70000" };
        var disks = LinuxSshMonitoringProvider.ParseDisk(lines);
        Assert.AreEqual(2, disks.Count);
        Assert.AreEqual("/", disks[0].Mount);
        Assert.AreEqual("/data", disks[1].Mount);
    }

    [TestMethod]
    public void ParseDisk_ConvertsFromMbToGb()
    {
        var lines = new[] { "/ 10240 5120" };
        var disks = LinuxSshMonitoringProvider.ParseDisk(lines);
        Assert.AreEqual(10.0, disks[0].TotalGb, 0.1);
        Assert.AreEqual(5.0, disks[0].UsedGb, 0.1);
    }

    // ── ParseLoad ────────────────────────────────────────────────────────────

    [TestMethod]
    public void ParseLoad_ReturnsThreeValues()
    {
        var (l1, l5, l15) = LinuxSshMonitoringProvider.ParseLoad("0.42 0.38 0.30 1/123 456");
        Assert.AreEqual(0.42, l1, 0.001);
        Assert.AreEqual(0.38, l5, 0.001);
        Assert.AreEqual(0.30, l15, 0.001);
    }

    // ── ParseDockerPs ────────────────────────────────────────────────────────

    [TestMethod]
    public void ParseDockerPs_WhenUnavailable_ReturnsEmpty()
    {
        var result = LinuxSshMonitoringProvider.ParseDockerPs(["unavailable"]);
        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public void ParseDockerPs_ReturnsNameStatusAndImage()
    {
        var lines = new[]
        {
            "myapp|Up 2 hours (healthy)|nginx:alpine",
            "mydb|Exited (0) 3 days ago|mariadb:10.11",
        };
        var result = LinuxSshMonitoringProvider.ParseDockerPs(lines);
        Assert.AreEqual(2, result.Count);
        Assert.IsTrue(result.ContainsKey("myapp"));
        Assert.AreEqual("nginx:alpine", result["myapp"].Image);
        Assert.AreEqual("Up 2 hours (healthy)", result["myapp"].Status);
    }

    // ── ParseDockerStats ─────────────────────────────────────────────────────

    [TestMethod]
    public void ParseDockerStats_ReturnsNameCpuAndMemory()
    {
        var lines = new[] { "myapp|12.34%|512MiB / 2GiB" };
        var result = LinuxSshMonitoringProvider.ParseDockerStats(lines);
        Assert.IsTrue(result.ContainsKey("myapp"));
        Assert.AreEqual(12.3, result["myapp"].CpuPercent, 0.1);
        Assert.AreEqual(512, result["myapp"].MemUsedMb);
        Assert.AreEqual(2048, result["myapp"].MemLimitMb);
    }

    [TestMethod]
    public void ParseDockerStats_WhenUnavailable_ReturnsEmpty()
    {
        var result = LinuxSshMonitoringProvider.ParseDockerStats(["unavailable"]);
        Assert.AreEqual(0, result.Count);
    }

    // ── MergeContainers ──────────────────────────────────────────────────────

    [TestMethod]
    public void MergeContainers_StoppedContainersHaveZeroResources()
    {
        var ps = new Dictionary<string, (string Status, string Image)>
        {
            ["stoppedapp"] = ("Exited (0) 2 days ago", "myimage:latest"),
        };
        var stats = new Dictionary<string, (double CpuPercent, long MemUsedMb, long MemLimitMb)>();
        var result = LinuxSshMonitoringProvider.MergeContainers(ps, stats);
        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(0.0, result[0].CpuPercent);
        Assert.AreEqual(0L, result[0].MemUsedMb);
        Assert.AreEqual("Stopped", result[0].Status);
    }

    [TestMethod]
    public void MergeContainers_RunningContainersGetResourcesFromStats()
    {
        var ps = new Dictionary<string, (string Status, string Image)>
        {
            ["webserver"] = ("Up 1 hour (healthy)", "nginx:alpine"),
        };
        var stats = new Dictionary<string, (double CpuPercent, long MemUsedMb, long MemLimitMb)>
        {
            ["webserver"] = (8.5, 256, 1024),
        };
        var result = LinuxSshMonitoringProvider.MergeContainers(ps, stats);
        Assert.AreEqual("Healthy", result[0].Status);
        Assert.AreEqual(8.5, result[0].CpuPercent, 0.01);
        Assert.AreEqual(256L, result[0].MemUsedMb);
    }

    // ── ParseSslCerts ────────────────────────────────────────────────────────

    [TestMethod]
    public void ParseSslCerts_WhenEmpty_ReturnsEmpty()
    {
        var result = LinuxSshMonitoringProvider.ParseSslCerts([]);
        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public void ParseSslCerts_WhenUnavailable_ReturnsEmpty()
    {
        var result = LinuxSshMonitoringProvider.ParseSslCerts(["unavailable"]);
        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public void ParseSslCerts_ParsesDomainAndDates()
    {
        var lines = new[] { "example.com|Jun 15 12:00:00 2024 GMT|Sep 13 12:00:00 2024 GMT" };
        var result = LinuxSshMonitoringProvider.ParseSslCerts(lines);
        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("example.com", result[0].Domain);
        Assert.AreEqual(new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Utc), result[0].IssuedUtc);
        Assert.AreEqual(new DateTime(2024, 9, 13, 12, 0, 0, DateTimeKind.Utc), result[0].ExpiresUtc);
    }

    [TestMethod]
    public void ParseSslCerts_HandlesSingleDigitDayWithDoubleSpace()
    {
        var lines = new[] { "example.com|Jun  5 12:00:00 2024 GMT|Sep  1 12:00:00 2024 GMT" };
        var result = LinuxSshMonitoringProvider.ParseSslCerts(lines);
        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(new DateTime(2024, 6, 5, 12, 0, 0, DateTimeKind.Utc), result[0].IssuedUtc);
        Assert.AreEqual(new DateTime(2024, 9, 1, 12, 0, 0, DateTimeKind.Utc), result[0].ExpiresUtc);
    }

    [TestMethod]
    public void ParseSslCerts_SkipsLinesWithBadDates()
    {
        var lines = new[] { "bad|notadate|Sep 13 12:00:00 2024 GMT", "ok|Jun 15 12:00:00 2024 GMT|Sep 13 12:00:00 2024 GMT" };
        var result = LinuxSshMonitoringProvider.ParseSslCerts(lines);
        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("ok", result[0].Domain);
    }

    [TestMethod]
    public void ParseSslCerts_ParsesMultipleCerts()
    {
        var lines = new[]
        {
            "example.com|Jun 15 12:00:00 2024 GMT|Sep 13 12:00:00 2024 GMT",
            "api.example.com|Jul  1 00:00:00 2024 GMT|Sep 29 00:00:00 2024 GMT",
        };
        var result = LinuxSshMonitoringProvider.ParseSslCerts(lines);
        Assert.AreEqual(2, result.Count);
        Assert.AreEqual("api.example.com", result[1].Domain);
        Assert.AreEqual(new DateTime(2024, 7, 1, 0, 0, 0, DateTimeKind.Utc), result[1].IssuedUtc);
    }

    // ── ParseFailedServices ──────────────────────────────────────────────────

    [TestMethod]
    public void ParseFailedServices_WhenEmpty_ReturnsEmpty()
    {
        var result = LinuxSshMonitoringProvider.ParseFailedServices([]);
        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public void ParseFailedServices_ReturnsServiceNames()
    {
        var lines = new[] { "nginx.service", "postgresql.service" };
        var result = LinuxSshMonitoringProvider.ParseFailedServices(lines);
        Assert.AreEqual(2, result.Count);
        Assert.AreEqual("nginx.service", result[0]);
        Assert.AreEqual("postgresql.service", result[1]);
    }

    [TestMethod]
    public void ParseFailedServices_SkipsBlankAndUnavailable()
    {
        var lines = new[] { "", "  ", "unavailable", "myapp.service" };
        var result = LinuxSshMonitoringProvider.ParseFailedServices(lines);
        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("myapp.service", result[0]);
    }

    // ── ParseOomEvents ───────────────────────────────────────────────────────

    [TestMethod]
    public void ParseOomEvents_WhenEmpty_ReturnsEmpty()
    {
        var result = LinuxSshMonitoringProvider.ParseOomEvents([]);
        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public void ParseOomEvents_ParsesProcessNameAndPid()
    {
        var lines = new[]
        {
            "[Fri Jun 14 10:23:45 2024] Out of memory: Killed process 1234 (nginx) total-vm:102400kB, anon-rss:51200kB",
        };
        var result = LinuxSshMonitoringProvider.ParseOomEvents(lines);
        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("nginx", result[0].ProcessName);
        Assert.AreEqual(1234L, result[0].Pid);
    }

    [TestMethod]
    public void ParseOomEvents_ParsesMemoryMb()
    {
        var lines = new[]
        {
            "[Fri Jun 14 10:23:45 2024] Out of memory: Killed process 5678 (java) total-vm:2048000kB, anon-rss:204800kB",
        };
        var result = LinuxSshMonitoringProvider.ParseOomEvents(lines);
        Assert.AreEqual(200L, result[0].MemoryMb); // 204800 kB / 1024 = 200 MB
    }

    [TestMethod]
    public void ParseOomEvents_HandlesLineWithoutDmesgTimestamp()
    {
        // Kernel uptime seconds format (no -T flag)
        var lines = new[]
        {
            "[ 12345.678901] Out of memory: Killed process 999 (oom_victim) total-vm:512kB, anon-rss:256kB",
        };
        var result = LinuxSshMonitoringProvider.ParseOomEvents(lines);
        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("oom_victim", result[0].ProcessName);
        Assert.IsNull(result[0].OccurredAt); // Can't parse uptime seconds as wall time
    }

    // ── ParseZombies ─────────────────────────────────────────────────────────

    [TestMethod]
    public void ParseZombies_WhenEmpty_ReturnsEmpty()
    {
        var result = LinuxSshMonitoringProvider.ParseZombies([]);
        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public void ParseZombies_ParsesPidProcessAndParent()
    {
        var lines = new[] { "1234|nginx|worker" };
        var result = LinuxSshMonitoringProvider.ParseZombies(lines);
        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(1234L, result[0].Pid);
        Assert.AreEqual("worker", result[0].ProcessName);
        Assert.AreEqual("nginx", result[0].ParentName);
    }

    [TestMethod]
    public void ParseZombies_SkipsInvalidLines()
    {
        var lines = new[] { "notanumber|parent|child", "5678|bash|defunct" };
        var result = LinuxSshMonitoringProvider.ParseZombies(lines);
        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(5678L, result[0].Pid);
        Assert.AreEqual("bash", result[0].ParentName);
    }
}
