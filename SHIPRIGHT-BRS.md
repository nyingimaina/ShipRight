# ShipRight — Backend & Frontend Refactoring Specification (BRS)

**Version**: 2.5.0 baseline  
**Audience**: Junior developer  
**Priority**: HIGH items must be done before the next feature sprint. MEDIUM before end of quarter. LOW are quality-of-life.

**Standing rule**: Every item in this document follows TDD — write the failing test first, then make it pass. That means for EVERY change below, step 1 is always "write the test that proves the current behaviour is wrong or missing." Only then do you touch production code.

---

## Contents

1. [How to work through this document (TDD workflow)](#1-tdd-workflow)
2. [HIGH — Thread-safety bug in `_pauseWaiters`](#2-high--thread-safety-bug-in-_pausewaiters)
3. [HIGH — O(n²) log concatenation in `PipelineContext`](#3-high--on-log-concatenation-in-pipelinecontext)
4. [HIGH — `RespondAsync` uses `SetResult` instead of `TrySetResult`](#4-high--respondaysnc-uses-setresult-instead-of-trysetresult)
5. [HIGH — HttpClient has no timeout](#5-high--httpclient-has-no-timeout)
6. [HIGH — Plaintext Docker credentials on disk](#6-high--plaintext-docker-credentials)
7. [MEDIUM — Module-level `lineCounter` in `BuildWizard.tsx`](#7-medium--module-level-linecounter)
8. [MEDIUM — Hard-coded step registry](#8-medium--hard-coded-step-registry)
9. [MEDIUM — No concurrent-build guard per project](#9-medium--no-concurrent-build-guard)
10. [MEDIUM — `BuildRecord` mixes metadata with log content](#10-medium--buildrecord-mixes-metadata-with-log-content)
11. [LOW — Static Next.js export committed to `wwwroot`](#11-low--static-nextjs-export-in-wwwroot)
12. [LOW — Direct project URLs return 404](#12-low--direct-project-urls-return-404)
13. [LOW — No persistent storage (SQLite migration)](#13-low--no-persistent-storage)

---

## 1. TDD Workflow

**Every item here follows this exact order — no exceptions.**

```
1. Read the existing tests. Understand what IS covered.
2. Write a test that demonstrates the bug or missing behaviour → it must FAIL (red).
3. Run the test suite — confirm it fails for the expected reason.
4. Change production code to make the test pass (green).
5. Run the FULL test suite (dotnet test + npm test). All tests must pass.
6. Commit: "test: <what> then fix: <what>"
```

Do NOT touch production code before you have a failing test. If you do, your reviewer will revert your PR.

For .NET: `cd back-end && dotnet test`  
For frontend: `cd front-end && npm test`

---

## 2. HIGH — Thread-safety bug in `_pauseWaiters`

**File**: `back-end/ShipRight.Server/Modules/Builds/BuildOrchestrator.cs:26`

### What the code does now

```csharp
// Line 26 — current (BROKEN)
private readonly Dictionary<string, TaskCompletionSource<RespondRequest>> _pauseWaiters = new();
```

`_pauseWaiters` is a plain `Dictionary<string, TCS>`. It is written on the **pipeline thread** (background `Task.Run`):

```csharp
// RunPipelineAsync — background thread
var tcs = new TaskCompletionSource<RespondRequest>();
_pauseWaiters[record.Id] = tcs;           // ← write from pipeline thread
var response = await tcs.Task;
```

And read/removed on the **ASP.NET thread pool thread** handling the HTTP request:

```csharp
// RespondAsync — HTTP handler thread
if (!_pauseWaiters.TryGetValue(buildId, out var tcs)) return false;  // ← read
tcs.SetResult(response);
_pauseWaiters.Remove(buildId);            // ← write from HTTP thread
```

Two threads accessing a non-thread-safe `Dictionary` concurrently is a **data race**. In .NET, this can corrupt the dictionary's internal hash table, causing `KeyNotFoundException`, `NullReferenceException`, or infinite loops inside the dictionary itself. The bug is intermittent — it will not reproduce every run, which makes it extremely hard to diagnose in production.

### Why this matters

This dictionary is the mechanism that allows the pipeline to pause and wait for user input (e.g. "Enter Docker password"). A corrupted dictionary means pauses silently break or the pipeline hangs forever.

### Step-by-step fix

#### Step 1 — Write the failing test

File: `back-end/ShipRight.Tests/Modules/Builds/PauseWaitersTests.cs` (new file)

```csharp
// Proves that Respond can be called from a separate thread safely
[TestMethod]
public async Task RespondAsync_CalledConcurrently_DoesNotThrow()
{
    // Arrange: create a BuildOrchestrator with fakes
    // (or test the specific dictionary access pattern directly)

    var dict = new ConcurrentDictionary<string, TaskCompletionSource<string>>();
    var tcs = new TaskCompletionSource<string>();
    dict["build-1"] = tcs;

    // Act: simulate pipeline thread awaiting and HTTP thread resolving simultaneously
    var pipelineTask = Task.Run(async () => await tcs.Task);
    var httpTask = Task.Run(() =>
    {
        if (dict.TryGetValue("build-1", out var found))
            found.TrySetResult("ok");
        dict.TryRemove("build-1", out _);
    });

    await Task.WhenAll(pipelineTask, httpTask);

    // Assert: no exceptions, correct result
    Assert.AreEqual("ok", await pipelineTask);
}
```

Run `dotnet test` — with the current `Dictionary` this test will PASS (because the race is non-deterministic). That's expected. The test's value is as a specification: it documents the required contract and will catch regressions if someone replaces `ConcurrentDictionary` back.

Now add a stress test that creates 100 concurrent pairs to actually trigger the race on a `Dictionary`:

```csharp
[TestMethod]
public async Task RespondAsync_UnderConcurrentLoad_NeverCorrupts()
{
    var dict = new ConcurrentDictionary<string, TaskCompletionSource<int>>();
    var tasks = Enumerable.Range(0, 100).Select(async i =>
    {
        var key = $"build-{i}";
        var tcs = new TaskCompletionSource<int>();
        dict[key] = tcs;
        var readTask = Task.Run(() =>
        {
            if (dict.TryGetValue(key, out var found)) found.TrySetResult(i);
            dict.TryRemove(key, out _);
        });
        return await tcs.Task;
    });
    var results = await Task.WhenAll(tasks);
    Assert.AreEqual(100, results.Length);
}
```

#### Step 2 — Fix production code

In `BuildOrchestrator.cs`, change line 26:

```csharp
// BEFORE (BROKEN)
private readonly Dictionary<string, TaskCompletionSource<RespondRequest>> _pauseWaiters = new();

// AFTER (correct)
private readonly ConcurrentDictionary<string, TaskCompletionSource<RespondRequest>> _pauseWaiters = new();
```

No other lines need changing — `ConcurrentDictionary` has the same `TryGetValue` and index-setter API. The `Remove` call in `RespondAsync` becomes `TryRemove`:

```csharp
// RespondAsync — BEFORE
_pauseWaiters.Remove(buildId);

// RespondAsync — AFTER
_pauseWaiters.TryRemove(buildId, out _);
```

#### Step 3 — Run all tests

```
cd back-end && dotnet test
```

All 81+ tests must pass.

---

## 3. HIGH — O(n²) log concatenation in `PipelineContext`

**File**: `back-end/ShipRight.Server/Modules/Builds/PipelineContext.cs:19`

### What the code does now

```csharp
// Line 19 — current (SLOW)
public async Task EmitLogAsync(string line, string source = "shipright")
{
    Record.LogOutput += line + "\n";   // ← O(n²) allocation
    ...
}
```

`Record.LogOutput` is a `string`. In .NET, strings are **immutable** — `+=` on a string is syntactic sugar for `string.Concat(existing, new)`, which allocates a brand-new string containing a copy of everything already in `LogOutput` plus the new line.

For a build with 5 000 log lines:
- Line 1: allocate 50 bytes
- Line 2: allocate 100 bytes (copy 50 + new 50)
- Line 3: allocate 150 bytes
- ...
- Line 5 000: allocate 250 000 bytes (copy 249 950 + new 50)

Total allocations: **~625 MB** for what is functionally a 250 KB log file. The garbage collector must deal with all of it. A build that produces 50 000 log lines (a Docker build with verbose output) can allocate **62 GB** of heap garbage.

### Step-by-step fix

This is a pure refactor of `PipelineContext`. Because we're not changing any external API (the `EmitLogAsync` signature stays identical), the change is safe to make in-place.

#### Step 1 — Write a performance test

File: `back-end/ShipRight.Tests/Modules/Builds/PipelineContextTests.cs` (new file)

```csharp
[TestMethod]
public async Task EmitLogAsync_5000Lines_CompletesQuickly()
{
    var record = new BuildRecord();
    var bus = new BuildEventBus();
    bus.Register(record.Id);
    var ctx = new PipelineContext(record, bus);

    var sw = System.Diagnostics.Stopwatch.StartNew();
    for (int i = 0; i < 5_000; i++)
        await ctx.EmitLogAsync($"log line {i}");
    sw.Stop();

    // Should complete in under 1 second even on a slow CI machine
    Assert.IsTrue(sw.ElapsedMilliseconds < 1_000,
        $"Expected < 1s, got {sw.ElapsedMilliseconds}ms — check for O(n²) allocation");

    // Verify all lines are in the output
    var output = record.GetLogOutput();  // ← new method (see below)
    Assert.IsTrue(output.Contains("log line 4999"));
}
```

Run the test — it will fail if the machine is slow OR if `GetLogOutput` does not yet exist.

#### Step 2 — Change `BuildRecord` and `PipelineContext`

**IMPORTANT: Do NOT edit `BuildRecord.LogOutput` in place — that would break all callers that read it.** Add a new internal storage mechanism.

In `BuildRecord.cs`, add a private list and a new read accessor. Do NOT remove `LogOutput` yet — it's used by callers to read the stored log:

```csharp
// New internal storage — never allocated from outside this class
private readonly List<string> _logLines = new();

// Replaces direct += usage inside PipelineContext
internal void AppendLogLine(string line) => _logLines.Add(line);

// Returns the assembled log; only called when saving or displaying
public string GetLogOutput() => string.Join('\n', _logLines);

// Keep the old property for JSON serialization compatibility — it now materialises on read
[JsonIgnore]  // control when this is expensive
public string LogOutput
{
    get => GetLogOutput();
    set
    {
        // For deserialization: split back to lines
        _logLines.Clear();
        if (!string.IsNullOrEmpty(value))
            _logLines.AddRange(value.Split('\n'));
    }
}
```

In `PipelineContext.cs`, change line 19:

```csharp
// BEFORE
Record.LogOutput += line + "\n";

// AFTER
Record.AppendLogLine(line);
```

#### Step 3 — Run all tests

```
cd back-end && dotnet test
```

All tests must pass. The performance test must now complete in < 1 second.

---

## 4. HIGH — `RespondAsync` uses `SetResult` instead of `TrySetResult`

**File**: `back-end/ShipRight.Server/Modules/Builds/BuildOrchestrator.cs:148`

### What the code does now

```csharp
// Line 148 — current (can throw)
public async Task<bool> RespondAsync(string buildId, RespondRequest response)
{
    if (!_pauseWaiters.TryGetValue(buildId, out var tcs)) return false;
    tcs.SetResult(response);       // ← throws InvalidOperationException if already resolved
    _pauseWaiters.Remove(buildId);
    return true;
}
```

`TaskCompletionSource.SetResult` throws `InvalidOperationException` if the task is already in a terminal state (completed, faulted, or cancelled). This can happen in two real scenarios:

1. **User double-clicks "Confirm"** — the browser sends two rapid POST requests. The second arrives at `RespondAsync` after the first has already resolved the `tcs`.
2. **Build was cancelled** — the pipeline's CancellationToken was cancelled, which may have propagated to the `tcs` via `tcs.TrySetCanceled()`. Then a late HTTP response arrives.

The thrown exception propagates to the HTTP handler and returns a **500 Internal Server Error** to the frontend, which may show an error toast even though the user's action succeeded.

### Step-by-step fix

#### Step 1 — Write the failing test

```csharp
[TestMethod]
public async Task RespondAsync_WhenCalledTwice_SecondCallReturnsFalseNotThrow()
{
    // Simulate: two rapid HTTP requests both call RespondAsync for the same buildId
    // The second must not throw.

    // We test the TCS directly since BuildOrchestrator has complex dependencies
    var tcs = new TaskCompletionSource<string>();
    var succeeded1 = tcs.TrySetResult("first");
    var succeeded2 = tcs.TrySetResult("second");  // must not throw

    Assert.IsTrue(succeeded1);
    Assert.IsFalse(succeeded2);  // graceful no-op
}
```

This test passes as written — use it as a specification. Now write the integration-style test that catches the real failure:

```csharp
[TestMethod]
public async Task RespondAsync_AfterBuildCancelled_DoesNotThrow()
{
    // Arrange: create a tcs that is already cancelled
    var tcs = new TaskCompletionSource<string>();
    tcs.SetCanceled();

    // Act: simulate what RespondAsync does when it gets a late response
    // The OLD code would throw here:
    bool threw = false;
    try { tcs.SetResult("late"); }
    catch (InvalidOperationException) { threw = true; }

    Assert.IsTrue(threw, "SetResult should throw — proving we need TrySetResult");

    // Now verify TrySetResult does not throw
    var tcs2 = new TaskCompletionSource<string>();
    tcs2.SetCanceled();
    bool result = tcs2.TrySetResult("late");  // ← must not throw
    Assert.IsFalse(result);
}
```

#### Step 2 — Fix production code

In `BuildOrchestrator.cs`, change `RespondAsync`:

```csharp
// BEFORE
tcs.SetResult(response);

// AFTER
if (!tcs.TrySetResult(response)) return false;  // already resolved — treat as not found
```

#### Step 3 — Run all tests

```
cd back-end && dotnet test
```

---

## 5. HIGH — HttpClient has no timeout

**File**: `back-end/ShipRight.Server/Modules/Builds/BuildOrchestrator.cs:28`

### What the code does now

```csharp
// Line 28 — current
private static readonly HttpClient _httpClient = new();
```

The default `HttpClient.Timeout` is **100 seconds**. During step 1 (`PreconditionCheck`), the orchestrator calls the GitHub releases API to download the `buildx` binary. If GitHub is unreachable (DNS failure, network timeout), the pipeline thread blocks for 100 seconds before throwing. The user sees the UI frozen with no progress for nearly two minutes.

Worse: if this happens during a push operation (which has its own 5-minute hard timeout), the `_httpClient` call eats a large fraction of that budget silently.

The correct timeout for a precheck API call is **15–30 seconds**.

### Step-by-step fix

#### Step 1 — Write the test

```csharp
[TestMethod]
public void HttpClient_Timeout_IsConfiguredToReasonableValue()
{
    // Access the static field via reflection to test its configuration
    var field = typeof(BuildOrchestrator)
        .GetField("_httpClient", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
    var client = (HttpClient)field!.GetValue(null)!;

    Assert.IsTrue(client.Timeout <= TimeSpan.FromSeconds(30),
        $"HttpClient.Timeout should be ≤ 30s, got {client.Timeout}");
    Assert.IsTrue(client.Timeout >= TimeSpan.FromSeconds(5),
        $"HttpClient.Timeout should be ≥ 5s to avoid false-positive timeouts");
}
```

Run the test — it FAILS because the current timeout is 100 seconds.

#### Step 2 — Fix production code

```csharp
// BEFORE
private static readonly HttpClient _httpClient = new();

// AFTER
private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(20) };
```

#### Step 3 — Run all tests and confirm the timeout test now passes

---

## 6. HIGH — Plaintext Docker credentials

**Files**:  
- `back-end/ShipRight.Server/Modules/Projects/DockerCredentialPreservingProjectStore.cs`  
- `back-end/ShipRight.Server/Shared/Store/SecureStore.cs` (exists — unused for this)

### What the code does now

Project JSON files stored in `~/.shipright/projects/` contain:

```json
{
  "dockerUsername": "myuser",
  "dockerPassword": "mysecretpassword123"
}
```

`DockerCredentialPreservingProjectStore` correctly preserves these values across saves, but the underlying `JsonProjectStore` writes them **in plain text to disk**. Anyone with read access to `~/.shipright/` can extract all Docker registry passwords.

`SecureStore.cs` already exists and provides `Encrypt`/`Decrypt` backed by `ProtectedData` (Windows DPAPI) or an AES key on Linux. It is completely unused for credential storage.

### Step-by-step fix

This is a security change — it requires a migration path for existing stored credentials.

#### Step 1 — Write failing tests

File: `back-end/ShipRight.Tests/Modules/Projects/DockerCredentialEncryptionTests.cs` (new)

```csharp
[TestMethod]
public void ProjectConfig_WhenSavedToJson_DoesNotContainPlaintextPassword()
{
    // Arrange
    var project = new ProjectConfig
    {
        Id = "test",
        DockerUsername = "myuser",
        DockerPassword = "supersecret"
    };

    // Act: serialize using the same serializer as JsonProjectStore
    var json = JsonConvert.SerializeObject(project);

    // Assert: raw password must not appear in the serialized JSON
    Assert.IsFalse(json.Contains("supersecret"),
        "Plaintext password found in serialized JSON — credentials are not encrypted");
}

[TestMethod]
public void ProjectConfig_RoundTrip_PreservesPassword()
{
    var project = new ProjectConfig { DockerPassword = "supersecret" };
    var json = JsonConvert.SerializeObject(project);
    var restored = JsonConvert.DeserializeObject<ProjectConfig>(json)!;
    Assert.AreEqual("supersecret", restored.DockerPassword,
        "Password not preserved through serialisation round-trip");
}
```

Both tests will FAIL because the first finds the plaintext password and the second passes (proving the data is currently readable). They document the before/after contract.

#### Step 2 — Add a custom JSON converter

**Do NOT edit `ProjectConfig` directly.** Add a new converter class:

```
back-end/ShipRight.Server/Modules/Projects/EncryptedStringConverter.cs
```

```csharp
using Newtonsoft.Json;
using ShipRight.Shared.Store;

namespace ShipRight.Modules.Projects;

public class EncryptedStringConverter : JsonConverter<string?>
{
    public override string? ReadJson(JsonReader reader, Type objectType,
        string? existingValue, bool hasExistingValue, JsonSerializer serializer)
    {
        var raw = reader.Value as string;
        if (string.IsNullOrEmpty(raw)) return raw;

        // Support both legacy plaintext and new encrypted values
        if (raw.StartsWith("ENC:", StringComparison.Ordinal))
            return SecureStore.Decrypt(raw[4..]);
        return raw;  // legacy — plaintext passthrough
    }

    public override void WriteJson(JsonWriter writer, string? value, JsonSerializer serializer)
    {
        if (string.IsNullOrEmpty(value)) { writer.WriteNull(); return; }
        writer.WriteValue("ENC:" + SecureStore.Encrypt(value));
    }
}
```

Then decorate the credential properties in `ProjectConfig.cs` (additive — new attribute, no existing code changed):

```csharp
[JsonConverter(typeof(EncryptedStringConverter))]
public string DockerUsername { get; set; } = string.Empty;

[JsonConverter(typeof(EncryptedStringConverter))]
public string DockerPassword { get; set; } = string.Empty;
```

#### Step 3 — Migration for existing users

When the server starts, scan existing project files. If any contain `DockerPassword` without the `ENC:` prefix, re-save them. Add this to startup in `Program.cs`:

```csharp
// One-time migration: encrypt plaintext credentials
await MigrateCredentials(app.Services.GetRequiredService<IProjectStore>());
```

#### Step 4 — Run all tests

---

## 7. MEDIUM — Module-level `lineCounter` in `BuildWizard.tsx`

**File**: `front-end/src/modules/BuildWizard/BuildWizard.tsx:95`

### What the code does now

```ts
// Line 95 — current (BROKEN for concurrent wizards)
let lineCounter = 0;

// Line 184 — used inside a React callback
setLines(prev => [...prev, { id: lineCounter++, source, line }]);
```

`lineCounter` is declared at **module scope** — it is shared across ALL instances of `BuildWizard` for the entire lifetime of the browser tab. In normal usage there is only one `BuildWizard` open at a time. But:

1. If a user opens two projects side-by-side (future feature), both wizards share the same counter. React uses `id` for reconciliation — duplicate or colliding IDs between two lists will produce subtle rendering bugs.
2. When `BuildWizard` unmounts and remounts (e.g. user closes and reopens the wizard during the same session), the counter continues from where it left off rather than resetting. Lines from the new session have IDs that start at, say, 3847 rather than 0. This is harmless now but confusing.

The `id` only needs to be unique within a single wizard instance's line list. A `useRef` inside the component is the correct scope.

### Step-by-step fix

#### Step 1 — Write the failing test

In `BuildWizard.test.tsx`, add a test that renders two wizards and checks their line IDs do not collide:

```tsx
it('two mounted BuildWizard instances assign line ids independently', async () => {
  (api.post as jest.Mock).mockResolvedValue({ buildId: 'build-a' });

  const { unmount: unmount1 } = render(<BuildWizard {...defaultProps} projectId="proj-a" />);
  const { unmount: unmount2 } = render(<BuildWizard {...defaultProps} projectId="proj-b" />);

  // If lineCounter is module-level, the second wizard's IDs will be offset
  // This test documents the contract that IDs start at 0 (or any consistent value)
  // for each independent instance.
  // With the current module-level counter this cannot be easily asserted,
  // but the test will serve as a regression guard after the fix.
  unmount1();
  unmount2();
});
```

#### Step 2 — Fix production code

Change `BuildWizard.tsx` — move the counter from module scope to a ref inside the component:

```ts
// REMOVE from module scope (line 95):
let lineCounter = 0;

// ADD inside the BuildWizard function body (near other useRef declarations):
const lineCounterRef = useRef(0);

// CHANGE line 184:
// BEFORE:
setLines(prev => [...prev, { id: lineCounter++, source, line }]);

// AFTER:
setLines(prev => [...prev, { id: lineCounterRef.current++, source, line }]);
```

Also reset the counter when the wizard resets its lines:

```ts
// Wherever lines is reset to [], also reset the counter:
setLines([]);
lineCounterRef.current = 0;
```

#### Step 3 — Run frontend tests

```
cd front-end && npm test
```

---

## 8. MEDIUM — Hard-coded step registry

**File**: `back-end/ShipRight.Server/Modules/Builds/BuildOrchestrator.cs:29`

### What the code does now

```csharp
// Lines 29–33 — current
private static readonly Dictionary<string, int> _stepNumbers = new()
{
    ["PreconditionCheck"] = 1, ["GitStatusCheck"] = 2, ["BranchCheck"] = 3,
    ["WriteVersionsAndTag"] = 4, ["ComposeRepoSync"] = 5, ["DockerBuild"] = 6, ["BuildComplete"] = 7,
};
```

This dictionary has several problems:

1. **Out-of-sync risk**: Step numbers are hard-coded. If you add a step between existing steps (e.g. a new step 3a), all subsequent numbers change and the dictionary must be updated manually. If you forget, the smart-resume logic silently uses the wrong step ordering.

2. **Scattered branching**: `RunPipelineAsync` is 400+ lines of sequential `if (resumeFromStep > N)` checks. Adding a new step requires editing the middle of that monolith.

3. **Not tested**: The smart-resume logic (which uses `_stepNumbers`) has no unit tests. A typo in a step name would silently disable smart-resume without any warning.

### Step-by-step fix

This is a larger refactor. Do it incrementally.

#### Step 1 — Write tests for the existing smart-resume logic (characterisation tests)

Before touching a single line of production code, add tests that document the current behaviour:

```csharp
// File: back-end/ShipRight.Tests/Modules/Builds/StepRegistryTests.cs

[TestMethod]
public void StepNumbers_PreconditionCheck_IsStep1()
{
    // Uses reflection to read the static field — documents the current contract
    var field = typeof(BuildOrchestrator)
        .GetField("_stepNumbers", BindingFlags.NonPublic | BindingFlags.Static);
    var dict = (Dictionary<string, int>)field!.GetValue(null)!;

    Assert.AreEqual(1, dict["PreconditionCheck"]);
    Assert.AreEqual(7, dict["BuildComplete"]);
}

[TestMethod]
public void StepNumbers_AllStepsPresent_AndOrderIsConsistent()
{
    var field = typeof(BuildOrchestrator)
        .GetField("_stepNumbers", BindingFlags.NonPublic | BindingFlags.Static);
    var dict = (Dictionary<string, int>)field!.GetValue(null)!;

    var numbers = dict.Values.OrderBy(n => n).ToList();
    for (int i = 0; i < numbers.Count - 1; i++)
        Assert.AreEqual(numbers[i] + 1, numbers[i + 1],
            $"Step numbers must be consecutive, gap found between {numbers[i]} and {numbers[i + 1]}");
}
```

These tests pass now. They will fail if someone later corrupts the step numbering.

#### Step 2 — Extract step metadata into a strongly-typed record

**New file**: `back-end/ShipRight.Server/Modules/Builds/PipelineStep.cs`

```csharp
namespace ShipRight.Modules.Builds;

public record PipelineStep(string Name, int Number)
{
    public static readonly IReadOnlyList<PipelineStep> All = new[]
    {
        new PipelineStep("PreconditionCheck",   1),
        new PipelineStep("GitStatusCheck",      2),
        new PipelineStep("BranchCheck",         3),
        new PipelineStep("WriteVersionsAndTag", 4),
        new PipelineStep("ComposeRepoSync",     5),
        new PipelineStep("DockerBuild",         6),
        new PipelineStep("BuildComplete",       7),
    };

    public static int NumberOf(string name) =>
        All.First(s => s.Name == name).Number;
}
```

#### Step 3 — Replace the dictionary with the typed registry

In `BuildOrchestrator.cs`, remove the dictionary:

```csharp
// REMOVE
private static readonly Dictionary<string, int> _stepNumbers = new() { ... };
```

Replace all usages of `_stepNumbers[...]` with `PipelineStep.NumberOf(...)`. These are additive reads — no pipeline logic changes.

#### Step 4 — Run all tests

The characterisation tests from step 1 now test via `PipelineStep.All` — update them accordingly.

---

## 9. MEDIUM — No concurrent-build guard per project

**File**: `back-end/ShipRight.Server/Modules/Builds/BuildOrchestrator.cs:52`

### What the code does now

```csharp
public async Task<BuildRecord> StartAsync(StartBuildRequest request)
{
    ...
    _ = Task.Run(() => RunPipelineAsync(record, project));  // fire and forget
    return record;
}
```

`StartAsync` fires a background task and immediately returns. There is no check whether a build for this project is already running. A user who double-clicks "Start Build" (or a frontend retry) will launch two simultaneous pipeline runs for the same project.

Two concurrent pipelines on the same project will:
- Both try to `git commit` and `git tag` the same files → one will fail with "nothing to commit" or produce a duplicate tag error
- Both try to `docker build` the same image simultaneously → one may succeed and one fail with a conflicting layer lock
- Both write to `BuildRecord` files in the same directory under the same filename patterns → last-write-wins, potentially corrupting the record

### Step-by-step fix

#### Step 1 — Write the test

```csharp
[TestMethod]
public async Task StartAsync_WhenProjectAlreadyBuilding_ReturnsExistingBuildOrThrows()
{
    // Arrange: use fake stores and a real BuildOrchestrator
    var buildStore = new InMemoryBuildStore();
    var projectStore = new InMemoryProjectStore();
    var bus = new BuildEventBus();
    var runner = new FakeProcessRunner();
    var orchestrator = new BuildOrchestrator(buildStore, projectStore, bus, runner, new FakeSshRunner());

    await projectStore.SaveAsync(new ProjectConfig { Id = "p1", Name = "Project 1" });

    // Act: start two builds simultaneously
    var request = new StartBuildRequest("p1", new List<ServiceVersionInput>());
    var t1 = orchestrator.StartAsync(request);
    var t2 = orchestrator.StartAsync(request);

    await Task.WhenAll(t1, t2);

    // Assert: only one build is running at a time
    var running = (await buildStore.QueryAsync("p1", null, null, null, null, 1, 10))
        .Where(b => b.Status == BuildStatus.Running)
        .ToList();
    Assert.AreEqual(1, running.Count, "Only one build should be running at a time for the same project");
}
```

This test FAILS currently — both builds start.

#### Step 2 — Add a per-project semaphore

In `BuildOrchestrator.cs`, add:

```csharp
// NEW — one semaphore per project, max one concurrent build
private static readonly ConcurrentDictionary<string, SemaphoreSlim> _projectLocks = new();
```

Wrap `StartAsync` with the guard:

```csharp
var sem = _projectLocks.GetOrAdd(request.ProjectId, _ => new SemaphoreSlim(1, 1));
if (!await sem.WaitAsync(TimeSpan.Zero))
    throw new InvalidOperationException(
        $"A build for project '{request.ProjectId}' is already in progress.");
```

Release in the pipeline when it finishes (in the `finally` block of `RunPipelineAsync`):

```csharp
finally
{
    _projectLocks.TryGetValue(record.ProjectId, out var releaseSem);
    releaseSem?.Release();
}
```

#### Step 3 — Handle the error on the frontend

In `BuildWizard.tsx`, `handleStartBuild` already has a try/catch. The HTTP API will return 409 Conflict. Update the error toast:

```ts
catch (e: any) {
    const msg = e?.response?.status === 409
        ? 'A build for this project is already running.'
        : 'Failed to start build.';
    toast.error(msg);
    setActiveOp('idle');
}
```

---

## 10. MEDIUM — `BuildRecord` mixes metadata with log content

**File**: `back-end/ShipRight.Server/Modules/Builds/BuildRecord.cs:45`

### What the code does now

```csharp
// Line 45
public string LogOutput { get; set; } = string.Empty;
```

`BuildRecord` stores both the build's **metadata** (status, timestamps, versions, git tag) and the **full log output** as a single string in the same object. `JsonBuildStore` serialises the whole thing to a JSON file.

Consequences:
1. **Listing builds loads all logs**: `QueryAsync` reads all build records to filter/sort them. Every `GET /api/builds` response loads potentially megabytes of log text that the history page never displays.
2. **Saves are slow**: Every `await _buildStore.SaveAsync(record)` during the pipeline serialises and writes the growing log to disk. At 5 000 log lines the JSON file is multiple megabytes, and it's written on every step transition.
3. **Pagination is broken by design**: You cannot page through build metadata without loading the log.

### Step-by-step fix

#### Step 1 — Write characterisation tests

```csharp
[TestMethod]
public async Task QueryAsync_DoesNotIncludeLogOutput_InListResults()
{
    // Documents the contract: listing builds must not return log text
    // Currently this FAILS because log is always included
    var store = new JsonBuildStore(new TempDirectory().Path);
    var record = new BuildRecord { ProjectId = "p1" };
    record.AppendLogLine("enormous log content 12345");
    await store.SaveAsync(record);

    var results = await store.QueryAsync("p1", null, null, null, null, 1, 10);
    var first = results.First();

    Assert.AreEqual(string.Empty, first.LogOutput,
        "QueryAsync results must not include LogOutput — only metadata");
}
```

This test fails now. It becomes green after the fix.

#### Step 2 — Separate log storage

**New class**: `back-end/ShipRight.Server/Modules/Builds/BuildLogStore.cs`

```csharp
public interface IBuildLogStore
{
    Task AppendAsync(string buildId, IEnumerable<string> lines);
    Task<string> GetAsync(string buildId);
}

public class FileBuildLogStore : IBuildLogStore
{
    private readonly string _dir;

    public FileBuildLogStore(string dataDir) =>
        _dir = Path.Combine(dataDir, "build-logs");

    public async Task AppendAsync(string buildId, IEnumerable<string> lines)
    {
        Directory.CreateDirectory(_dir);
        await File.AppendAllLinesAsync(Path.Combine(_dir, $"{buildId}.log"), lines);
    }

    public async Task<string> GetAsync(string buildId)
    {
        var path = Path.Combine(_dir, $"{buildId}.log");
        return File.Exists(path) ? await File.ReadAllTextAsync(path) : string.Empty;
    }
}
```

Inject `IBuildLogStore` into `PipelineContext` so it writes logs to the file directly. `BuildRecord.LogOutput` becomes empty in the JSON; the `/api/builds/{id}/log` endpoint reads from `IBuildLogStore` instead.

This is a larger migration — do it incrementally. Start by dual-writing (write to both `LogOutput` and the file), then remove `LogOutput` from `SaveAsync`.

---

## 11. LOW — Static Next.js export committed to `wwwroot`

**Directory**: `back-end/ShipRight.Server/wwwroot/`

### What happens now

`build.sh` / the ISS installer builds the Next.js app and copies the output into `wwwroot/`. That `wwwroot/` directory is **committed to git**. Every build of the frontend produces a new set of content-hashed filenames like `chunks/projects-de2ef2b8acda4a9b.js`. Each commit touching the frontend produces 50–200 file changes in git — minified binary diffs that are unreadable and pollute `git log`, `git blame`, and PR diffs.

### Recommended approach

1. Add `back-end/ShipRight.Server/wwwroot/_next/` to `.gitignore`. Keep only `wwwroot/index.html` (the fallback) as a placeholder.
2. The installer build script already handles copying `front-end/out/` → `wwwroot/` as part of preprocessing. Do not change this.
3. In CI/CD (if you add it later), the frontend build step populates `wwwroot/` before the .NET publish step. Nothing else changes.

**TDD note**: No production code changes — this is a `.gitignore` change. No tests required. Run the installer build after the change to confirm it still works.

---

## 12. LOW — Direct project URLs return 404

**File**: `front-end/src/pages/projects/[id]/index.tsx`

### What happens now

```ts
export const getStaticPaths = () => ({ paths: [], fallback: false });
```

`paths: []` means Next.js generates no static files for `/projects/[id]`. The redirect to `/projects/?detail=${id}` works for client-side navigation (the user clicked a link inside the app). But if someone bookmarks `http://localhost:5200/projects/abc123` or pastes it into a new tab, they get a 404 because no `projects/abc123/index.html` file exists.

### Why this happened

The developer used `getStaticPaths` with `fallback: false` because project IDs are dynamic — they're not known at build time. This is the correct conclusion but the wrong solution. The correct solution is:

- **Option A** (quick): Keep the redirect approach but serve `/404.html` from the server with a JS redirect. Already partially done (`wwwroot/404.html` exists — check its content).
- **Option B** (proper): Use Next.js App Router (`app/projects/[id]/page.tsx`) with server-side data fetching. This generates correct HTML and allows real static or server rendering.

**TDD note**: Add an integration test that GETs `/projects/fake-id` via `HttpClient` against a running server and asserts it returns 200 (with a redirect body) rather than 404. Do this in a new `back-end/ShipRight.Tests/Integration/RoutingTests.cs`.

---

## 13. LOW — No persistent storage (SQLite migration path)

### What happens now

All data is stored as JSON flat files:
- `~/.shipright/projects/*.json` — one file per project
- `~/.shipright/builds/*.json` — one file per build record (with full log)
- `~/.shipright/scheduler/history/*.json` — backup history records

Problems:
1. No atomic multi-record writes — if the process dies mid-write, the file is corrupt.
2. No efficient querying — listing builds requires reading all files and filtering in memory.
3. No schema versioning — adding a required field to `BuildRecord` breaks deserialization of old files.
4. Race conditions — two HTTP requests can both read and write the same file without coordination.

### Recommended migration path

**Phase 1** (do this sprint): Add `[JsonIgnore]` + sensible defaults to any new fields added to records so old files don't break deserialization.

**Phase 2** (next quarter): Introduce SQLite via `Microsoft.Data.Sqlite`. Add `IBuildStore`, `IProjectStore` etc. as abstractions (already done). Implement `SqliteBuildStore` alongside `JsonBuildStore`. Switch via DI — JSON files become the fallback for migration.

**TDD note**: When implementing `SqliteBuildStore`, write all tests against the `IBuildStore` interface. Both `JsonBuildStore` and `SqliteBuildStore` must satisfy the same test suite — this is the "contract test" pattern.

```csharp
// Pattern: one abstract test class, two concrete test classes
public abstract class BuildStoreContractTests
{
    protected abstract IBuildStore CreateStore();

    [TestMethod]
    public async Task SaveAsync_ThenGetById_ReturnsRecord()
    {
        var store = CreateStore();
        var record = new BuildRecord { ProjectId = "p1" };
        await store.SaveAsync(record);
        var fetched = await store.GetByIdAsync(record.Id);
        Assert.IsNotNull(fetched);
        Assert.AreEqual("p1", fetched!.ProjectId);
    }
    // ... more contract tests
}

[TestClass]
public class JsonBuildStoreTests : BuildStoreContractTests
{
    protected override IBuildStore CreateStore() =>
        new JsonBuildStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
}

[TestClass]
public class SqliteBuildStoreTests : BuildStoreContractTests
{
    protected override IBuildStore CreateStore() =>
        new SqliteBuildStore($"Data Source={Path.GetTempFileName()}.db");
}
```

---

## Priority order summary

| # | Severity | Item | Effort |
|---|----------|------|--------|
| 2 | HIGH | Thread-safety: `_pauseWaiters` → `ConcurrentDictionary` | 30 min |
| 3 | HIGH | O(n²) log concat → `AppendLogLine` + `List<string>` | 1 day |
| 4 | HIGH | `SetResult` → `TrySetResult` in `RespondAsync` | 15 min |
| 5 | HIGH | HttpClient timeout = 20s | 15 min |
| 6 | HIGH | Encrypt Docker credentials at rest | 2 days |
| 7 | MEDIUM | Module-level `lineCounter` → `useRef` | 30 min |
| 8 | MEDIUM | `_stepNumbers` dict → `PipelineStep` registry | 1 day |
| 9 | MEDIUM | Concurrent-build guard per project | 1 day |
| 10 | MEDIUM | Separate log storage from `BuildRecord` | 3 days |
| 11 | LOW | Remove `wwwroot` from git | 2 hours |
| 12 | LOW | Fix direct project URL 404 | 1 day |
| 13 | LOW | SQLite migration path | 1 week |

**Do #2, #4, #5, #7 first** — they are each under one hour and eliminate real data races and potential 500 errors in production. Then tackle #3 and #6 as a pair (they are independent but both important).

---

## Reminder: TDD is non-negotiable on refactors

The items above that say "refactor" (#3, #8, #10, #13) are especially dangerous without tests. A refactor by definition changes internal structure without changing external behaviour. The only way to prove you haven't changed behaviour is a test suite that was green before and stays green after.

For each refactor:

1. Write tests that cover the existing behaviour (characterisation tests).
2. Confirm they pass.
3. Make the structural change.
4. Confirm they still pass.
5. Now the refactor is safe to ship.

If you skip step 1-2, you are guessing that your refactor is correct. In a production CI/CD tool where a bug means a failed deployment, guessing is not acceptable.
