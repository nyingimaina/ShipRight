using System.Text;
using ShipRight.Shared.ProcessRunner;

namespace ShipRight.Modules.Setup;

/// <summary>
/// Carries out the items of a <see cref="SetupPlan"/> the user approved. Create-only: a file that exists
/// by the time it is written is never overwritten, and nothing is committed unless asked.
/// </summary>
public sealed class SetupApplier
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly IProcessRunner _runner;

    public SetupApplier(IProcessRunner runner) => _runner = runner;

    public async Task<ApplyResult> ApplyAsync(SetupPlan plan, ApplyOptions options, CancellationToken ct = default)
    {
        var approved = new HashSet<string>(options.ApprovedItemIds, StringComparer.Ordinal);
        var results = new List<ApplyItemResult>();
        var createdFiles = new List<string>();

        foreach (var item in plan.Items)
        {
            if (item.Status == PlanItemStatus.Exists)
            {
                results.Add(new ApplyItemResult(item.Id, ApplyOutcome.SkippedExists, "Already there, left untouched."));
                continue;
            }
            if (!approved.Contains(item.Id))
            {
                results.Add(new ApplyItemResult(item.Id, ApplyOutcome.SkippedNotApproved, "Not selected."));
                continue;
            }

            var result = item.Kind == PlanItemKind.GitRepo
                ? await InitGitAsync(plan, item, ct)
                : WriteFile(plan, item);
            results.Add(result);
            if (result.Outcome == ApplyOutcome.Created && item.Kind != PlanItemKind.GitRepo)
                createdFiles.Add(item.RelativePath);
        }

        if (!options.CommitCreatedFiles || createdFiles.Count == 0)
            return new ApplyResult(results, false, null);

        var commit = await new GitCommitter(_runner)
            .CommitAsync(plan.RepoRoot, "chore: add ShipRight setup files", createdFiles, ct);
        return new ApplyResult(results, commit.Success, commit.Error);
    }

    private async Task<ApplyItemResult> InitGitAsync(SetupPlan plan, PlanItem item, CancellationToken ct)
    {
        var git = Path.Combine(plan.RepoRoot, ".git");
        if (Directory.Exists(git) || File.Exists(git))
            return new ApplyItemResult(item.Id, ApplyOutcome.SkippedExists, "A git repository is already here.");

        var init = await _runner.RunAsync("git", ["-C", plan.RepoRoot, "init", "--initial-branch=main"], null, ct: ct);
        if (!init.Success)
            init = await _runner.RunAsync("git", ["-C", plan.RepoRoot, "init"], null, ct: ct); // git older than 2.28

        return init.Success
            ? new ApplyItemResult(item.Id, ApplyOutcome.Created, "Created a git repository.")
            : new ApplyItemResult(item.Id, ApplyOutcome.Failed,
                (string.IsNullOrWhiteSpace(init.StdErr) ? init.StdOut : init.StdErr).Trim());
    }

    private static ApplyItemResult WriteFile(SetupPlan plan, PlanItem item)
    {
        var full = Path.GetFullPath(item.Path);
        if (!IsInside(plan.RepoRoot, full))
            return new ApplyItemResult(item.Id, ApplyOutcome.Failed, "Refused: this path is outside the project.");
        if (item.Content is null)
            return new ApplyItemResult(item.Id, ApplyOutcome.Failed, "Nothing to write for this item.");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            using var stream = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream, Utf8NoBom);
            writer.Write(item.Content.Replace("\r\n", "\n"));
            return new ApplyItemResult(item.Id, ApplyOutcome.Created, $"Created {item.RelativePath}.");
        }
        catch (IOException) when (File.Exists(full))
        {
            return new ApplyItemResult(item.Id, ApplyOutcome.SkippedExists, "Already there, left untouched.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ApplyItemResult(item.Id, ApplyOutcome.Failed, ex.Message);
        }
    }

    private static bool IsInside(string repoRoot, string fullPath)
    {
        var root = Path.GetFullPath(repoRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                   + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return fullPath.StartsWith(root, comparison);
    }
}
