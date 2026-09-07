using System.Collections.Concurrent;
using ShipRight.Modules.Builds;
using ShipRight.Modules.Projects;
using ShipRight.Modules.Resources.Models;
using ShipRight.Modules.Resources.Stores;
using ShipRight.Shared.CommandExecution;
using ShipRight.Shared.ProcessRunner;

namespace ShipRight.Modules.Resources;

/// <summary>
/// Enforces per-service image retention on Amazon ECR after each successful push.
/// Core: FIFO tag pruning newer-than (really older-than) the keep count.
/// Belt: idempotent lifecycle policy so the ECR side enforces the same bound
/// between pushes. Never throws — failures are emitted as log lines and skipped.
/// </summary>
public sealed class RegistryRotationCoordinator
{
    public const int DefaultRetentionCount = 5;
    public const int MaxBatchDeleteCount = 100;

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> RepoLocks = new();

    private readonly AwsEcrRegistryClient _client;
    private readonly IAwsProfileResourceStore? _profileStore;
    private readonly IBuildStore? _buildStore;

    public RegistryRotationCoordinator(
        IProcessRunner? runner, IAwsProfileResourceStore? profileStore,
        IBuildStore? buildStore, ICommandExecutor? executor = null)
    {
        _profileStore = profileStore;
        _buildStore = buildStore;
        _client = new AwsEcrRegistryClient(runner, executor);
    }

    /// <summary>
    /// Derives the ECR repository name from a registry host + docker image.
    /// "080632633137.dkr.ecr.us-east-2.amazonaws.com/ship-right/app" → "ship-right/app"
    /// "registry.example.com/team/worker" → "team/worker"; bare "org/app" → "org/app".
    /// </summary>
    public static string ExtractRepositoryName(string registryHost, string imageName)
    {
        if (string.IsNullOrWhiteSpace(imageName)) return string.Empty;
        var firstSlash = imageName.IndexOf('/');
        if (firstSlash < 0) return imageName;
        var prefix = imageName[..firstSlash];
        var isHostPrefix = prefix.Contains('.') || prefix.Contains(':');
        return isHostPrefix ? imageName[(firstSlash + 1)..] : imageName;
    }

    public async Task PruneAfterPushAsync(
        string projectId, ServiceConfig svc, ServiceVersion sv,
        DockerRegistryResource? resource, Func<string, Task> emitLog, CancellationToken ct = default)
    {
        try
        {
            var registry = RegistryHostResolver.Resolve(svc, resource);
            if (!AwsEcrAuthProvider.IsAwsEcrHost(registry))
            {
                await emitLog($"[ShipRight] Registry retention: {registry} is not Amazon ECR — skipped for {svc.Name}.");
                return;
            }

            var count = svc.ImageRetentionCount ?? DefaultRetentionCount;
            if (count == 0)
            {
                await emitLog($"[ShipRight] Registry retention: keep-all configured for {svc.Name} — skipped.");
                return;
            }

            var repository = ExtractRepositoryName(registry, svc.DockerImageName);
            if (string.IsNullOrEmpty(repository))
            {
                await emitLog($"[ShipRight] Registry retention: could not determine ECR repository from '{svc.DockerImageName}' — skipped.");
                return;
            }

            var region = !string.IsNullOrEmpty(resource?.AwsRegion)
                ? resource!.AwsRegion
                : AwsEcrAuthProvider.ParseRegionFromHost(registry);
            if (string.IsNullOrEmpty(region))
            {
                await emitLog($"[ShipRight] Registry retention: no AWS region for {registry} — set AwsRegion on the registry resource to enable retention.");
                return;
            }

            var env = await AwsEcrEnvResolver.ResolveAsync(_profileStore, resource);
            var lockKey = $"ecr/{region}/{repository}";
            var sem = RepoLocks.GetOrAdd(lockKey, _ => new SemaphoreSlim(1, 1));
            await sem.WaitAsync(ct);
            try
            {
                // Belt: keep the lifecycle policy present and in sync with the desired count.
                if (await _client.HasLifecyclePolicyAsync(repository, region, env, ct))
                {
                    await emitLog($"[ShipRight] Registry retention: ECR lifecycle policy already present for {repository}.");
                }
                else
                {
                    await _client.PutLifecyclePolicyAsync(repository, region, count, env, ct);
                    await emitLog($"[ShipRight] Registry retention: applied ECR lifecycle policy (keep newest {count}) for {repository}.");
                }

                // Core: FIFO prune anything older than the newest `count` tags.
                var images = await _client.DescribeImagesAsync(repository, region, env, ct);
                if (images.Count == 0)
                {
                    await emitLog($"[ShipRight] Registry retention: no tagged images found for {repository}.");
                    return;
                }

                var protectedTags = await ComputeProtectedTagsAsync(projectId, svc, sv, images);
                var toDelete = SelectDeletionCandidates(images.Select(i => i.Tag).ToList(), count, protectedTags);
                if (toDelete.Count == 0)
                {
                    await emitLog($"[ShipRight] Registry retention: nothing to prune for {repository} (keeping newest {count}).");
                    return;
                }

                await emitLog($"[ShipRight] Registry retention: pruning {toDelete.Count} old tag(s) from {repository} (keeping newest {count}).");
                foreach (var batch in toDelete.Chunk(MaxBatchDeleteCount))
                {
                    var failed = await _client.DeleteImagesAsync(repository, region, batch.ToList(), env, ct);
                    foreach (var tag in batch)
                    {
                        if (failed.Contains(tag)) continue;
                        await emitLog($"[ShipRight] Registry retention: pruned {repository}:{tag}.");
                    }
                    if (failed.Count > 0)
                        await emitLog($"[ShipRight] Registry retention: {failed.Count} tag(s) could not be pruned from {repository}.");
                }
            }
            finally
            {
                sem.Release();
            }
        }
        catch (Exception ex)
        {
            await emitLog($"[ShipRight] Registry retention failed for {svc.Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Tags that must never be pruned: the tag this build just shipped, "latest",
    /// and the previous Deployed/PushSucceeded version for the service
    /// (rollback pin). Distinct and newest-first already handled by the caller.
    /// </summary>
    internal async Task<HashSet<string>> ComputeProtectedTagsAsync(
        string projectId, ServiceConfig svc, ServiceVersion sv, List<EcrImageRef> images)
    {
        var protect = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            sv.NewVersion,
            "latest",
        };

        if (_buildStore is not null)
        {
            var recent = await _buildStore.QueryAsync(projectId, null, null, null, null, 1, 25);
            foreach (var record in recent)
            {
                if (record.Status is not (BuildStatus.PushSucceeded or BuildStatus.Deployed)) continue;
                var previous = record.Versions
                    .FirstOrDefault(v => v.ServiceName.Equals(svc.Name, StringComparison.OrdinalIgnoreCase) &&
                                         v.NewVersion != sv.NewVersion);
                if (previous is not null)
                {
                    protect.Add(previous.NewVersion);
                    break;
                }
            }
        }

        return protect;
    }

    /// <summary>
    /// Newest-first tag list → tags to delete. Protected tags are always kept;
    /// of the rest, the newest `keepCount` are kept and everything else is deleted.
    /// </summary>
    internal static List<string> SelectDeletionCandidates(
        IReadOnlyList<string> tagsNewestFirst, int keepCount, ISet<string> protectedTags)
    {
        var toDelete = new List<string>();
        var kept = 0;
        foreach (var tag in tagsNewestFirst)
        {
            if (protectedTags.Contains(tag)) continue;
            if (kept < keepCount) { kept++; continue; }
            if (!toDelete.Contains(tag)) toDelete.Add(tag);
        }
        return toDelete;
    }
}