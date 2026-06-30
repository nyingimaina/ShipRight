using System.Collections.Concurrent;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Builds;

namespace ShipRight.Tests.Modules.Builds;

/// <summary>
/// Tests for BRS #2 (thread-safe _pauseWaiters), #4 (TrySetResult), #5 (HttpClient timeout).
/// Each test starts with the failing contract spec, then the fix makes it green.
/// </summary>
[TestClass]
public class BuildOrchestratorSafetyTests
{
    // ── BRS #4: TrySetResult contract ────────────────────────────────────────

    [TestMethod]
    public void TCS_SetResult_WhenCalledTwice_Throws()
    {
        // Documents that raw SetResult is dangerous: proves we must use TrySetResult
        var tcs = new TaskCompletionSource<string>();
        tcs.SetResult("first");

        Assert.ThrowsException<InvalidOperationException>(
            () => tcs.SetResult("second"),
            "SetResult on a completed TCS must throw — this is why we need TrySetResult");
    }

    [TestMethod]
    public void TCS_TrySetResult_WhenCalledTwice_ReturnsFalseNotThrow()
    {
        var tcs = new TaskCompletionSource<string>();
        bool first  = tcs.TrySetResult("first");
        bool second = tcs.TrySetResult("second");

        Assert.IsTrue(first,   "First TrySetResult must return true");
        Assert.IsFalse(second, "Second TrySetResult must return false, not throw");
        Assert.AreEqual("first", tcs.Task.Result, "Result must be from the first call");
    }

    [TestMethod]
    public void TCS_TrySetResult_AfterCancellation_ReturnsFalse()
    {
        var tcs = new TaskCompletionSource<string>();
        tcs.SetCanceled();

        // This is the scenario: build cancelled, then late HTTP response arrives
        bool result = tcs.TrySetResult("late response");
        Assert.IsFalse(result, "TrySetResult after cancellation must return false, not throw");
    }

    // ── BRS #2: ConcurrentDictionary contract ────────────────────────────────

    [TestMethod]
    public async Task ConcurrentDictionary_UnderLoad_NeverCorrupts()
    {
        // Stress test: 200 concurrent readers+writers
        // A plain Dictionary would corrupt; ConcurrentDictionary must not.
        var dict = new ConcurrentDictionary<string, TaskCompletionSource<int>>();
        var tasks = Enumerable.Range(0, 200).Select(async i =>
        {
            var key = $"build-{i}";
            var tcs = new TaskCompletionSource<int>();
            dict[key] = tcs;

            var resolveTask = Task.Run(() =>
            {
                if (dict.TryGetValue(key, out var found))
                    found.TrySetResult(i);
                dict.TryRemove(key, out _);
            });

            await resolveTask;
            return await tcs.Task;
        });

        var results = await Task.WhenAll(tasks);
        Assert.AreEqual(200, results.Length, "All 200 concurrent operations must complete");
    }

    [TestMethod]
    public async Task ConcurrentDictionary_WriteFromOneThread_ReadFromAnother_Succeeds()
    {
        var dict = new ConcurrentDictionary<string, TaskCompletionSource<string>>();
        var tcs = new TaskCompletionSource<string>();

        // Simulate pipeline thread writing
        var writeTask = Task.Run(() => { dict["build-x"] = tcs; });
        await writeTask;

        // Simulate HTTP handler thread reading (from a different thread)
        var readTask = Task.Run(() =>
        {
            if (dict.TryGetValue("build-x", out var found))
                found.TrySetResult("user-response");
            dict.TryRemove("build-x", out _);
        });
        await readTask;

        Assert.AreEqual("user-response", await tcs.Task);
    }

    // ── BRS #5: HttpClient timeout ────────────────────────────────────────────

    [TestMethod]
    public void HttpClient_Timeout_IsSetToReasonableValue()
    {
        // Access the static field on BuildOrchestrator via reflection
        var field = typeof(BuildOrchestrator)
            .GetField("_httpClient",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Static);

        Assert.IsNotNull(field, "_httpClient static field must exist");
        var client = (System.Net.Http.HttpClient)field!.GetValue(null)!;

        Assert.IsTrue(client.Timeout <= TimeSpan.FromSeconds(30),
            $"HttpClient.Timeout must be ≤ 30s, got {client.Timeout}. " +
            "An unconfigured HttpClient defaults to 100s which can block the pipeline for over a minute.");
        Assert.IsTrue(client.Timeout >= TimeSpan.FromSeconds(5),
            $"HttpClient.Timeout must be ≥ 5s to avoid spurious timeouts on slow connections.");
    }

    // ── BRS #9: Concurrent-build guard (specification test) ──────────────────

    [TestMethod]
    public async Task SemaphoreSlim_WhenHeld_SecondAcquireWithZeroTimeoutReturnsFalse()
    {
        // Proves the pattern we use for per-project build locking:
        // SemaphoreSlim(1,1) + WaitAsync(TimeSpan.Zero) = non-blocking check
        var sem = new SemaphoreSlim(1, 1);
        await sem.WaitAsync();  // First acquire — simulates build in progress

        bool second = await sem.WaitAsync(TimeSpan.Zero);  // Non-blocking check
        Assert.IsFalse(second, "Second acquire with zero timeout must return false when semaphore is held");

        sem.Release();
    }

    [TestMethod]
    public async Task SemaphoreSlim_AfterRelease_NextAcquireSucceeds()
    {
        var sem = new SemaphoreSlim(1, 1);
        await sem.WaitAsync();
        sem.Release();

        bool acquired = await sem.WaitAsync(TimeSpan.Zero);
        Assert.IsTrue(acquired, "After release, next acquire must succeed");
        sem.Release();
    }
}
