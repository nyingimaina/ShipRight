using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Shared.Events;

namespace ShipRight.Tests.Modules.Builds;

[TestClass]
public class BuildEventBusTests
{
    [TestMethod]
    public async Task Register_WhenCalledTwiceForSameBuildId_ResetsStateForSecondOperation()
    {
        var bus = new BuildEventBus();
        const string buildId = "build-1";

        // First operation (build phase): register, emit, and complete
        bus.Register(buildId);
        await bus.EmitAsync(buildId, "LogLine", new { line = "build log" });
        bus.Complete(buildId);

        // Second operation (push phase): register again for the same buildId
        bus.Register(buildId);
        await bus.EmitAsync(buildId, "LogLine", new { line = "push log" });

        // A new subscriber should receive only push-phase events, not immediately complete
        var reader = bus.Subscribe(buildId);

        var received = new List<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try
        {
            // Read the buffered push-phase event
            var msg = await reader.ReadAsync(cts.Token);
            received.Add(msg);
        }
        catch (OperationCanceledException) { /* timed out — no more events */ }

        bus.Complete(buildId);

        Assert.AreEqual(1, received.Count, "Should receive exactly the push-phase event, not the build-phase event");
        StringAssert.Contains(received[0], "push log");
    }

    [TestMethod]
    public async Task Register_SecondRegistration_ChannelDoesNotImmediatelyComplete()
    {
        var bus = new BuildEventBus();
        const string buildId = "build-2";

        // First operation: complete it
        bus.Register(buildId);
        bus.Complete(buildId);

        // Second operation: register fresh
        bus.Register(buildId);

        var reader = bus.Subscribe(buildId);
        await bus.EmitAsync(buildId, "StepStarted", new { stepName = "DockerBuild" });
        bus.Complete(buildId);

        // Should be able to read the event (channel was not pre-completed)
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var msg = await reader.ReadAsync(cts.Token);
        StringAssert.Contains(msg, "StepStarted");
    }

    [TestMethod]
    public async Task Subscribe_BeforeAnyEvents_ReceivesBufferedEvents()
    {
        var bus = new BuildEventBus();
        const string buildId = "build-3";

        bus.Register(buildId);
        await bus.EmitAsync(buildId, "LogLine", new { line = "line-1" });
        await bus.EmitAsync(buildId, "LogLine", new { line = "line-2" });

        // Late subscriber — should receive both buffered events
        var reader = bus.Subscribe(buildId);
        bus.Complete(buildId);

        var received = new List<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await foreach (var msg in reader.ReadAllAsync(cts.Token).WithCancellation(cts.Token))
            received.Add(msg);

        Assert.AreEqual(2, received.Count);
    }
}
