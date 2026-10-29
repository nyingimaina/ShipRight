using System.Collections.Concurrent;
using ShipRight.Modules.Builds;
using ShipRight.Modules.Projects;
using ShipRight.Shared.CommandExecution;
using ShipRight.Shared.ProcessRunner;

namespace ShipRight.Modules.Resources;

/// <summary>Images (repo:tag) that were pushed to a registry successfully while ShipRight has been running.</summary>
public sealed class PushLedger
{
    private readonly ConcurrentDictionary<string, byte> _pushed = new(StringComparer.OrdinalIgnoreCase);

    public void RecordPush(string imageRef) => _pushed[imageRef] = 0;

    public bool WasPushed(string imageRef) => _pushed.ContainsKey(imageRef);

    public void Forget(string imageRef) => _pushed.TryRemove(imageRef, out _);
}

/// <summary>
/// Notes every <c>docker push</c> that exits cleanly, then passes everything through untouched. The registry then
/// holds a copy, which is what makes it safe to delete the local one.
/// </summary>
public sealed class PushRecordingProcessRunner : IProcessRunner
{
    private readonly IProcessRunner _inner;
    private readonly PushLedger _ledger;

    public PushRecordingProcessRunner(IProcessRunner inner, PushLedger ledger)
    {
        _inner = inner;
        _ledger = ledger;
    }

    public async Task<ProcessResult> RunAsync(string executable, string[] args, string? workingDir,
        Func<string, Task>? onOutput = null, Func<string, Task>? onError = null, CancellationToken ct = default,
        IReadOnlyDictionary<string, string>? envOverride = null, TimeSpan? timeout = null, string? stdin = null)
    {
        var result = await _inner.RunAsync(executable, args, workingDir, onOutput, onError, ct, envOverride, timeout, stdin);
        if (result.Success && executable == "docker" && args.Length == 2 && args[0] == "push")
            _ledger.RecordPush(args[1]);
        return result;
    }
}

/// <summary>
/// The local image cleanup, with two additions. A service can opt out of it completely
/// (<see cref="ServiceConfig.SkipLocalCleanup"/>). And with a keep count of 0 the image that was just shipped is
/// removed too - but only once the push really succeeded, because before that the local image is the only copy.
/// Images that a container uses are never removed.
/// </summary>
public class PushAwareImagePruner : BuildMachineImagePruner
{
    private readonly ICommandExecutor _docker;
    private readonly PushLedger _ledger;

    public PushAwareImagePruner(IProcessRunner runner, IBuildStore? buildStore, PushLedger ledger)
        : base(runner, buildStore)
    {
        _docker = CommandExecutor.PassthroughLocal(runner);
        _ledger = ledger;
    }

    public override async Task PruneLocalImagesAsync(
        ProjectConfig project, ServiceConfig svc, ServiceVersion sv,
        Func<string, Task> emitLog, CancellationToken ct = default)
    {
        if (svc.SkipLocalCleanup == true)
        {
            await emitLog($"[ShipRight] Local prune: {svc.Name} has opted out of local image cleanup - skipped.");
            return;
        }

        await base.PruneLocalImagesAsync(project, svc, sv, emitLog, ct);

        if ((svc.LocalImageKeepCount ?? DefaultLocalImageKeepCount) != 0) return;

        var shipped = $"{svc.DockerImageName}:{sv.NewVersion}";
        if (!_ledger.WasPushed(shipped)) return;

        try
        {
            var inUse = await _docker.RunAsync("docker", ["ps", "-a", "--format", "{{.Image}}"], null,
                timeout: TimeSpan.FromSeconds(30), ct: ct);
            if (inUse.Success && inUse.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Contains(shipped, StringComparer.OrdinalIgnoreCase))
            {
                await emitLog($"[ShipRight] Local prune: kept {shipped} - a container is using it.");
                return;
            }

            var removed = await _docker.RunAsync("docker", ["rmi", shipped], null, timeout: TimeSpan.FromSeconds(30), ct: ct);
            if (removed.Success)
            {
                _ledger.Forget(shipped);
                await emitLog($"[ShipRight] Local prune: removed {shipped} after it was pushed (the registry has it).");
            }
            else
            {
                await emitLog($"[ShipRight] Local prune: could not remove {shipped}.");
            }
        }
        catch (Exception ex)
        {
            await emitLog($"[ShipRight] Local prune failed for {svc.Name}: {ex.Message}");
        }
    }
}
