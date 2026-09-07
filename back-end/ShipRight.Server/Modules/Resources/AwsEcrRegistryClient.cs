using System.Text.Json;
using ShipRight.Shared.CommandExecution;
using ShipRight.Shared.ProcessRunner;

namespace ShipRight.Modules.Resources;

public sealed record EcrImageRef(string Tag, long PushedAtUtcMilliseconds);

/// <summary>
/// Runs `aws ecr` CLI commands for registry image retention: listing images
/// (describe-images), read/write of the lifecycle policy, and batch deletion.
/// All calls are issued through ICommandExecutor so they resolve the daemon /
/// aws CLI location exactly the way the build pipeline does.
/// </summary>
public sealed class AwsEcrRegistryClient
{
    private static readonly TimeSpan AwsCliTimeout = TimeSpan.FromSeconds(120);
    private readonly ICommandExecutor _executor;

    public AwsEcrRegistryClient(IProcessRunner? runner, ICommandExecutor? executor = null)
    {
        _executor = executor ?? (runner is null ? null! : CommandExecutor.PassthroughLocal(runner));
    }

    /// <summary>True when the repository already has a lifecycle policy.</summary>
    public async Task<bool> HasLifecyclePolicyAsync(
        string repository, string region, IReadOnlyDictionary<string, string>? env, CancellationToken ct = default)
    {
        var result = await RunAsync(
            ["ecr", "get-lifecycle-policy", "--repository-name", repository, "--region", region],
            env, ct);
        return result.Success;
    }

    /// <summary>Idempotent — (re)applies a keep-newest-N lifecycle policy to the repository.</summary>
    public async Task PutLifecyclePolicyAsync(
        string repository, string region, int countNumber,
        IReadOnlyDictionary<string, string>? env, CancellationToken ct = default)
    {
        var policyJson = JsonSerializer.Serialize(new
        {
            rules = new[]
            {
                new
                {
                    rulePriority = 1,
                    description = "ShipRight managed retention — keep newest tags",
                    selection = new
                    {
                        tagStatus = "tagged",
                        tagPatternList = new[] { "*" },
                        countType = "imageCountMoreThan",
                        countNumber,
                    },
                    action = new { type = "expire" },
                }
            }
        });

        var result = await RunAsync(
            ["ecr", "put-lifecycle-policy", "--repository-name", repository, "--region", region,
             "--lifecycle-policy-text", policyJson],
            env, ct);

        if (!result.Success)
            throw new InvalidOperationException(
                $"aws ecr put-lifecycle-policy failed for '{repository}' (exit {result.ExitCode}): {result.StdErr.Trim()}");
    }

    /// <summary>
    /// Returns all tagged images for the repository, newest-first by pushedAt,
    /// deduped by tag (the newest pushedAt wins for a tag shared across digests).
    /// </summary>
    public async Task<List<EcrImageRef>> DescribeImagesAsync(
        string repository, string region, IReadOnlyDictionary<string, string>? env, CancellationToken ct = default)
    {
        var result = await RunAsync(
            ["ecr", "describe-images", "--repository-name", repository, "--region", region],
            env, ct);

        if (!result.Success)
            throw new InvalidOperationException(
                $"aws ecr describe-images failed for '{repository}' (exit {result.ExitCode}): {result.StdErr.Trim()}");

        var byTag = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(result.StdOut);
        if (!doc.RootElement.TryGetProperty("imageDetails", out var details))
            return [];

        foreach (var detail in details.EnumerateArray())
        {
            if (!detail.TryGetProperty("imageTags", out var tagsEl)) continue;
            var pushedAt = detail.TryGetProperty("imagePushedAt", out var pushedEl) && pushedEl.TryGetInt64(out var ms)
                ? ms
                : 0L;
            foreach (var tagEl in tagsEl.EnumerateArray())
            {
                var tag = tagEl.GetString();
                if (string.IsNullOrEmpty(tag)) continue;
                if (!byTag.TryGetValue(tag, out var current) || pushedAt > current)
                    byTag[tag] = pushedAt;
            }
        }

        return byTag
            .OrderByDescending(kv => kv.Value)
            .Select(kv => new EcrImageRef(kv.Key, kv.Value))
            .ToList();
    }

    /// <summary>Batch-deletes tags (≤100 per call — ECR limit) and returns the tags that failed.</summary>
    public async Task<List<string>> DeleteImagesAsync(
        string repository, string region, IReadOnlyList<string> tags,
        IReadOnlyDictionary<string, string>? env, CancellationToken ct = default)
    {
        if (tags.Count == 0) return [];

        var args = new List<string>
        {
            "ecr", "batch-delete-image",
            "--repository-name", repository, "--region", region,
        };
        foreach (var tag in tags)
        {
            args.Add("--image-ids");
            args.Add($"imageTag={tag}");
        }

        var result = await RunAsync(args, env, ct);
        if (!result.Success)
            throw new InvalidOperationException(
                $"aws ecr batch-delete-image failed for '{repository}' (exit {result.ExitCode}): {result.StdErr.Trim()}");

        var failed = new List<string>();
        using var doc = JsonDocument.Parse(result.StdOut);
        if (doc.RootElement.TryGetProperty("failures", out var failures))
        {
            foreach (var entry in failures.EnumerateArray())
            {
                if (entry.TryGetProperty("imageId", out var idEl) &&
                    idEl.TryGetProperty("imageTag", out var tagEl) &&
                    tagEl.GetString() is { } tag)
                    failed.Add(tag);
            }
        }

        return failed;
    }

    private async Task<ProcessResult> RunAsync(
        IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? env, CancellationToken ct)
    {
        if (_executor is null)
            throw new InvalidOperationException("No process runner is available for ECR registry operations.");
        return await _executor.RunAsync("aws", args.ToArray(), null, timeout: AwsCliTimeout, envOverride: env, ct: ct);
    }
}