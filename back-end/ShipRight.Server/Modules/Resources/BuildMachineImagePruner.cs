using ShipRight.Modules.Builds;
using ShipRight.Modules.Projects;
using ShipRight.Shared.CommandExecution;
using ShipRight.Shared.ProcessRunner;

namespace ShipRight.Modules.Resources;

/// <summary>
/// Frees disk space on the build machine after a build/push: removes unused
/// images for a project's services (keep-window from build history), prunes
/// dangling images, and trims the BuildKit cache to the configured budget.
/// Never throws — failures are logged per image and execution continues.
/// </summary>
public sealed class BuildMachineImagePruner
{
    public const int DefaultLocalImageKeepCount = 2;

    private readonly ICommandExecutor _executor;
    private readonly IBuildStore? _buildStore;

    public BuildMachineImagePruner(IProcessRunner? runner, IBuildStore? buildStore, ICommandExecutor? executor = null)
    {
        _buildStore = buildStore;
        _executor = executor ?? (runner is null ? null! : CommandExecutor.PassthroughLocal(runner));
    }

    public async Task PruneLocalImagesAsync(
        ProjectConfig project, ServiceConfig svc, ServiceVersion sv,
        Func<string, Task> emitLog, CancellationToken ct = default)
    {
        try
        {
            if (_executor is null)
            {
                await emitLog($"[ShipRight] Local prune: no process runner available for {svc.Name} — skipped.");
                return;
            }

            var imageRef = svc.DockerImageName;
            if (string.IsNullOrWhiteSpace(imageRef))
            {
                await emitLog($"[ShipRight] Local prune: no docker image name for service {svc.Name} — skipped.");
                return;
            }

            var keepCount = svc.LocalImageKeepCount ?? DefaultLocalImageKeepCount;
            var languageLine = await LogDockerDfAsync(emitLog, ct);
            await emitLog($"[ShipRight] Local prune for {imageRef}: keeping {keepCount} recent version(s)" +
                          (languageLine.Length > 0 ? $" — {languageLine}." : "."));

            var localRefs = await ListLocalImageRefsAsync(imageRef, ct);
            if (localRefs.Count == 0)
            {
                await emitLog($"[ShipRight] Local prune: no local images for {imageRef}.");
                return;
            }

            var inUseRefs = await ListInUseImageRefsAsync(ct);
            var keepTags = await ComputeKeepWindowAsync(project, svc, sv, keepCount, ct);
            var keepRefs = keepTags.Select(t => $"{imageRef}:{t}").ToHashSet(StringComparer.OrdinalIgnoreCase);

            var candidates = SelectLocalPruneCandidates(localRefs, keepRefs, inUseRefs);
            if (candidates.Count == 0)
            {
                await emitLog($"[ShipRight] Local prune: nothing to remove for {imageRef}.");
            }
            else
            {
                foreach (var candidate in candidates)
                {
                    var result = await _executor.RunAsync(
                        "docker", ["rmi", candidate], null, timeout: TimeSpan.FromSeconds(30), ct: ct);
                    if (result.Success)
                        await emitLog($"[ShipRight] Local prune: removed {candidate}.");
                    else
                        await emitLog($"[ShipRight] Local prune: could not remove {candidate} (in use?).");
                }
            }

            var dangling = await _executor.RunAsync(
                "docker", ["image", "prune", "-f"], null, timeout: TimeSpan.FromSeconds(60), ct: ct);
            if (dangling.Success && !string.IsNullOrEmpty(dangling.StdOut.Trim()))
                await emitLog($"[ShipRight] Local prune: {dangling.StdOut.Trim().Split('\n').Last().Trim()}");
            else if (!dangling.Success)
                await emitLog($"[ShipRight] Local prune: dangling-image prune failed (exit {dangling.ExitCode}).");

            if (project.LocalCachePruneKeepGb > 0)
            {
                var pruneCache = await _executor.RunAsync(
                    "docker", ["builder", "prune", "-f", "--keep-storage", $"{project.LocalCachePruneKeepGb}gb"],
                    null, timeout: TimeSpan.FromSeconds(120), ct: ct);
                if (pruneCache.Success && !string.IsNullOrEmpty(pruneCache.StdOut.Trim()))
                    await emitLog($"[ShipRight] Local prune: {pruneCache.StdOut.Trim().Split('\n').Last().Trim()} freed from build cache.");
            }

            var afterLine = await LogDockerDfAsync(emitLog, ct);
            if (afterLine.Length > 0)
                await emitLog($"[ShipRight] Local prune complete — {afterLine}.");
        }
        catch (Exception ex)
        {
            await emitLog($"[ShipRight] Local prune failed for {svc.Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Distinct version tags to keep locally: the `keepCount` most recent distinct
    /// pushed/deployed versions from build history plus the tag just shipped.
    /// </summary>
    internal async Task<List<string>> ComputeKeepWindowAsync(
        ProjectConfig project, ServiceConfig svc, ServiceVersion sv, int keepCount, CancellationToken ct = default)
    {
        var keep = new List<string> { sv.NewVersion };
        if (_buildStore is not null)
        {
            var recent = await _buildStore.QueryAsync(project.Id, null, null, null, null, 1, 50);
            foreach (var record in recent)
            {
                if (record.Status is not (BuildStatus.PushSucceeded or BuildStatus.Deployed or BuildStatus.ImageBuilt)) continue;
                foreach (var version in record.Versions)
                {
                    if (!version.ServiceName.Equals(svc.Name, StringComparison.OrdinalIgnoreCase)) continue;
                    if (version.NewVersion == sv.NewVersion) continue;
                    if (keep.Count >= keepCount) return keep;
                    if (!keep.Contains(version.NewVersion)) keep.Add(version.NewVersion);
                }
                if (keep.Count >= keepCount) return keep;
            }
        }
        return keep;
    }

    /// <summary>
    /// Local refs to remove: repo:tag present on the machine that are neither in
    /// the keep window nor referenced by a container (running or stopped).
    /// </summary>
    internal static List<string> SelectLocalPruneCandidates(
        IEnumerable<string> localRefs, ISet<string> keepRefs, ISet<string> inUseRefs)
    {
        return localRefs
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(r => !keepRefs.Contains(r) && !inUseRefs.Contains(r))
            .ToList();
    }

    private async Task<List<string>> ListLocalImageRefsAsync(string imageRef, CancellationToken ct)
    {
        var result = await _executor.RunAsync(
            "docker",
            ["images", "--no-trunc", "--filter", $"reference={imageRef}", "--format", "{{.Repository}}:{{.Tag}}"],
            null, timeout: TimeSpan.FromSeconds(30), ct: ct);
        if (!result.Success) return [];

        return result.StdOut
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.EndsWith(":<none>", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private async Task<HashSet<string>> ListInUseImageRefsAsync(CancellationToken ct)
    {
        var result = await _executor.RunAsync(
            "docker", ["ps", "-a", "--format", "{{.Image}}"], null,
            timeout: TimeSpan.FromSeconds(30), ct: ct);
        if (!result.Success) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return result.StdOut
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private async Task<string> LogDockerDfAsync(Func<string, Task> emitLog, CancellationToken ct)
    {
        var df = await _executor.RunAsync("docker", ["system", "df"], null,
            timeout: TimeSpan.FromSeconds(30), ct: ct);
        if (df.Success)
        {
            var lines = df.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var imageRow = lines.FirstOrDefault(l => l.StartsWith("Images", StringComparison.OrdinalIgnoreCase));
            return imageRow ?? string.Empty;
        }
        return string.Empty;
    }
}