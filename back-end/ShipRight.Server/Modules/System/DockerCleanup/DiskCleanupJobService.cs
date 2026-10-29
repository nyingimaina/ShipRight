using ShipRight.Modules.Builds;
using ShipRight.Modules.System.WslDisk;

namespace ShipRight.Modules.System.DockerCleanup;

/// <summary>A build that is in the way of stopping WSL, with what the person needs to deal with it.</summary>
public record ActiveBuild(string Id, string ProjectName, string Status, string? Step, DateTime StartedAt, bool CanAbort);

public static class ActiveBuilds
{
    public static async Task<IReadOnlyList<ActiveBuild>> ListAsync(IBuildStore builds)
    {
        var found = new List<ActiveBuild>();
        foreach (var status in new[] { BuildStatus.Running, BuildStatus.Paused, BuildStatus.Pending })
        {
            foreach (var b in await builds.QueryAsync(null, status.ToString(), null, null, null, 1, 100))
                found.Add(new ActiveBuild(b.Id, b.ProjectName, status.ToString(), b.CurrentStepName, b.StartedAt,
                    CanAbort: status is BuildStatus.Running or BuildStatus.Paused));
        }
        return found;
    }
}

public record DiskJobRequest(IReadOnlyCollection<string>? Images, bool PruneDangling, bool PruneBuildCache, bool Shrink);

public enum DiskJobStatus { Running, Succeeded, Failed, Blocked }

public enum DiskStepStatus { Pending, Running, Done, Failed, Skipped }

public record DiskJobStep(
    string Id, string Label, DiskStepStatus Status, DateTime? StartedAt, DateTime? FinishedAt,
    int EstimatedSeconds,   // a guess made before the step starts - never a promise
    string? Detail);

public record DiskJob(
    string Id, DiskJobStatus Status, DateTime StartedAt, DateTime? FinishedAt,
    IReadOnlyList<DiskJobStep> Steps, IReadOnlyList<ActiveBuild> Blockers,
    CleanupResult? Cleanup, WslCompactResult? Shrink, string? Error);

/// <summary>
/// Runs "free up disk space" as a background job the page can watch: separate steps with their own timings and
/// estimates, so a long shrink shows where it is instead of a spinner. Only one job runs at a time. If the job
/// includes the shrink and a build is active, it stops before deleting anything and names the build, because
/// stopping WSL would kill it.
/// </summary>
public class DiskCleanupJobService
{
    private sealed class MutableStep
    {
        public string Id = "", Label = "";
        public DiskStepStatus Status;
        public DateTime? StartedAt, FinishedAt;
        public int EstimatedSeconds;
        public string? Detail;
    }

    private sealed class Job
    {
        public string Id = Guid.NewGuid().ToString("N");
        public DiskJobStatus Status = DiskJobStatus.Running;
        public DateTime StartedAt = DateTime.UtcNow;
        public DateTime? FinishedAt;
        public List<MutableStep> Steps = [];
        public List<ActiveBuild> Blockers = [];
        public CleanupResult? Cleanup;
        public WslCompactResult? Shrink;
        public string? Error;
        public Task Completion = Task.CompletedTask;
    }

    private readonly DockerCleanupService _cleanup;
    private readonly IBuildStore _builds;
    private readonly Func<CancellationToken, Action<string>, Task<WslCompactResult>> _shrink;
    private readonly Func<CancellationToken, Task<long>> _diskBytes;
    private readonly Func<IReadOnlyList<string>>? _searchedPlaces;
    private readonly object _gate = new();
    private Job? _latest;

    public DiskCleanupJobService(
        DockerCleanupService cleanup, IBuildStore builds,
        Func<CancellationToken, Action<string>, Task<WslCompactResult>> shrink,
        Func<CancellationToken, Task<long>> diskBytes,
        Func<IReadOnlyList<string>>? searchedPlaces = null)
    {
        _cleanup = cleanup;
        _builds = builds;
        _shrink = shrink;
        _diskBytes = diskBytes;
        _searchedPlaces = searchedPlaces;
    }

    public DiskJob Start(DiskJobRequest request)
    {
        lock (_gate)
        {
            if (_latest is { Status: DiskJobStatus.Running }) return Snapshot(_latest);

            var job = new Job { Steps = PlanSteps(request) };
            _latest = job;
            job.Completion = Task.Run(() => RunAsync(job, request));
            return Snapshot(job);
        }
    }

    public async Task<DiskJob> StartAndWaitAsync(DiskJobRequest request)
    {
        var started = Start(request);
        await WaitAsync(started.Id);
        return Get(started.Id)!;
    }

    public async Task WaitAsync(string id)
    {
        Task? t;
        lock (_gate) t = _latest is { } j && j.Id == id ? j.Completion : null;
        if (t is not null) await t;
    }

    public async Task WaitForStepAsync(string id, string stepId)
    {
        for (var i = 0; i < 500; i++)
        {
            var job = Get(id);
            if (job is null) return;
            var step = job.Steps.FirstOrDefault(s => s.Id == stepId);
            if (step is null || step.Status != DiskStepStatus.Pending || job.Status != DiskJobStatus.Running) return;
            await Task.Delay(10);
        }
    }

    public DiskJob? Latest()
    {
        lock (_gate) return _latest is null ? null : Snapshot(_latest);
    }

    public DiskJob? Get(string id)
    {
        lock (_gate) return _latest is { } j && j.Id == id ? Snapshot(j) : null;
    }

    private List<MutableStep> PlanSteps(DiskJobRequest r)
    {
        var steps = new List<MutableStep>();
        var count = r.Images?.Count ?? 0;
        if (count > 0) steps.Add(new() { Id = "images", Label = $"Removing {count} image{(count == 1 ? "" : "s")}", EstimatedSeconds = 5 + 4 * count });
        if (r.PruneDangling) steps.Add(new() { Id = "leftovers", Label = "Clearing leftover layers", EstimatedSeconds = 15 });
        if (r.PruneBuildCache) steps.Add(new() { Id = "cache", Label = "Clearing the build cache", EstimatedSeconds = 30 });
        if (r.Shrink) steps.Add(new() { Id = "shrink", Label = "Shrinking your Windows disk", EstimatedSeconds = 300 });
        return steps;
    }

    private async Task RunAsync(Job job, DiskJobRequest request)
    {
        try
        {
            if (request.Shrink)
            {
                var blockers = await ActiveBuilds.ListAsync(_builds);
                if (blockers.Count > 0)
                {
                    lock (_gate)
                    {
                        job.Blockers = blockers.ToList();
                        foreach (var s in job.Steps) s.Status = DiskStepStatus.Skipped;
                        job.Error = "A build is running or waiting for you. Stopping WSL would interrupt it, so nothing was changed.";
                        job.Status = DiskJobStatus.Blocked;
                    }
                    return;
                }
                long? bytes = null;
                try { bytes = await _diskBytes(CancellationToken.None); }
                catch (Exception) { /* size unknown: still try the shrink, it will report its own problem */ }

                if (bytes is <= 0)
                {
                    // No disk file means the shrink cannot work, so stop before deleting anything for nothing.
                    var where = _searchedPlaces?.Invoke() ?? [];
                    lock (_gate)
                    {
                        foreach (var st in job.Steps) st.Status = DiskStepStatus.Skipped;
                        job.Error = "ShipRight could not find the WSL disk file, so there is nothing to shrink." +
                                    (where.Count > 0 ? " It looked in: " + string.Join("; ", where) + "." : "") +
                                    " Nothing was stopped or deleted.";
                        job.Status = DiskJobStatus.Failed;
                    }
                    return;
                }
                if (bytes is { } known)
                    lock (_gate) job.Steps.First(s => s.Id == "shrink").EstimatedSeconds = 60 + (int)(4 * (known / (1024.0 * 1024 * 1024)));
            }

            var items = new List<CleanupItemResult>();
            string? dangling = null, cache = null, before = null, after = null;

            foreach (var step in job.Steps)
            {
                lock (_gate) { step.Status = DiskStepStatus.Running; step.StartedAt = DateTime.UtcNow; }
                var failure = await RunStepAsync(job, step, request, items, r => { dangling = r.DanglingOutput ?? dangling; cache = r.BuildCacheOutput ?? cache; before ??= r.Before; after = r.After; });
                lock (_gate)
                {
                    step.FinishedAt = DateTime.UtcNow;
                    step.Status = failure is null ? DiskStepStatus.Done : DiskStepStatus.Failed;
                    step.Detail = failure ?? step.Detail;
                    job.Cleanup = new CleanupResult(items, dangling, cache, before ?? "", after ?? "");
                    if (failure is not null)
                    {
                        job.Error = failure;
                        job.Status = DiskJobStatus.Failed;
                        foreach (var rest in job.Steps.Where(s => s.Status == DiskStepStatus.Pending)) rest.Status = DiskStepStatus.Skipped;
                        return;
                    }
                }
            }
            lock (_gate) job.Status = DiskJobStatus.Succeeded;
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                job.Error = ex.Message;
                job.Status = DiskJobStatus.Failed;
                foreach (var s in job.Steps.Where(s => s.Status is DiskStepStatus.Running)) { s.Status = DiskStepStatus.Failed; s.FinishedAt = DateTime.UtcNow; }
                foreach (var s in job.Steps.Where(s => s.Status is DiskStepStatus.Pending)) s.Status = DiskStepStatus.Skipped;
            }
        }
        finally
        {
            lock (_gate) job.FinishedAt = DateTime.UtcNow;
        }
    }

    /// <summary>Null when the step worked; otherwise the plain-words reason it did not.</summary>
    private async Task<string?> RunStepAsync(Job job, MutableStep step, DiskJobRequest request,
        List<CleanupItemResult> items, Action<CleanupResult> record)
    {
        switch (step.Id)
        {
            case "images":
            {
                var r = await _cleanup.RunAsync(new CleanupRequest(request.Images, false, false), CancellationToken.None);
                items.AddRange(r.Items);
                record(r);
                return null;
            }
            case "leftovers":
            {
                var r = await _cleanup.RunAsync(new CleanupRequest([], true, false), CancellationToken.None);
                record(r);
                return r.DanglingOutput?.StartsWith("Failed", StringComparison.Ordinal) == true ? $"Leftover layers: {r.DanglingOutput}" : null;
            }
            case "cache":
            {
                var r = await _cleanup.RunAsync(new CleanupRequest([], false, true), CancellationToken.None);
                record(r);
                return r.BuildCacheOutput?.StartsWith("Failed", StringComparison.Ordinal) == true ? $"Build cache: {r.BuildCacheOutput}" : null;
            }
            default: // shrink
            {
                var result = await _shrink(CancellationToken.None, line => { lock (_gate) step.Detail = line; });
                lock (_gate) job.Shrink = result;
                return result.Succeeded ? null : $"{result.BlockedReason ?? "The disk could not be shrunk."} {string.Join(" ", result.Messages)}".Trim();
            }
        }
    }

    private static DiskJob Snapshot(Job j) => new(
        j.Id, j.Status, j.StartedAt, j.FinishedAt,
        j.Steps.Select(s => new DiskJobStep(s.Id, s.Label, s.Status, s.StartedAt, s.FinishedAt, s.EstimatedSeconds, s.Detail)).ToList(),
        j.Blockers.ToList(), j.Cleanup, j.Shrink, j.Error);
}
