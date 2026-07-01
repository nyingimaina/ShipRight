using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Builds;
using ShipRight.Shared.Events;

namespace ShipRight.Tests.Modules.Builds;

[TestClass]
public class PipelineContextTests
{
    [TestMethod]
    public async Task EmitLogAsync_5000Lines_CompletesInUnder2Seconds()
    {
        var record = new BuildRecord();
        var bus = new BuildEventBus();
        bus.Register(record.Id);
        var ctx = new PipelineContext(record, bus);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 5_000; i++)
            await ctx.EmitLogAsync($"log line {i}");
        sw.Stop();

        Assert.IsTrue(sw.ElapsedMilliseconds < 2_000,
            $"Expected < 2s, got {sw.ElapsedMilliseconds}ms — likely O(n²) string allocation");

        var output = record.GetLogOutput();
        Assert.IsTrue(output.Contains("log line 4999"), "Last line must be present");
        Assert.IsTrue(output.Contains("log line 0"), "First line must be present");
    }

    [TestMethod]
    public async Task EmitLogAsync_Lines_AreOrderedCorrectly()
    {
        var record = new BuildRecord();
        var bus = new BuildEventBus();
        bus.Register(record.Id);
        var ctx = new PipelineContext(record, bus);

        await ctx.EmitLogAsync("first");
        await ctx.EmitLogAsync("second");
        await ctx.EmitLogAsync("third");

        var output = record.GetLogOutput();
        var idx1 = output.IndexOf("first", StringComparison.Ordinal);
        var idx2 = output.IndexOf("second", StringComparison.Ordinal);
        var idx3 = output.IndexOf("third", StringComparison.Ordinal);

        Assert.IsTrue(idx1 < idx2, "first must appear before second");
        Assert.IsTrue(idx2 < idx3, "second must appear before third");
    }

    [TestMethod]
    public async Task BuildRecord_GetLogOutput_ReturnsAllAppendedLines()
    {
        var record = new BuildRecord();
        record.AppendLogLine("alpha");
        record.AppendLogLine("beta");
        record.AppendLogLine("gamma");

        var output = record.GetLogOutput();
        StringAssert.Contains(output, "alpha");
        StringAssert.Contains(output, "beta");
        StringAssert.Contains(output, "gamma");
    }

    [TestMethod]
    public void BuildRecord_LogOutput_RoundTripPreservesContent()
    {
        var record = new BuildRecord();
        record.AppendLogLine("line one");
        record.AppendLogLine("line two");

        // Simulate JSON serialization round-trip via the property setter
        var serialized = record.GetLogOutput();
        var restored = new BuildRecord();
        restored.LogOutput = serialized;

        Assert.AreEqual(record.GetLogOutput(), restored.GetLogOutput());
    }

    [TestMethod]
    public void BuildRecord_LogOutput_EmptyByDefault()
    {
        var record = new BuildRecord();
        Assert.AreEqual(string.Empty, record.GetLogOutput());
        Assert.AreEqual(string.Empty, record.LogOutput);
    }
}
