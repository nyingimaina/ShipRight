namespace ShipRight.Modules.Setup;

public enum Confidence { Low, Medium, High }

/// <summary>One deployable thing found in the user's folder (an API, a website, a worker).</summary>
public record DetectedComponent(
    string StackId,                              // "dotnet", "node"
    string Kind,                                 // "aspnet-web", "nextjs", "spa", ...
    string Name,                                 // suggested service name
    string Directory,                            // absolute project folder
    string ContextDirectory,                     // absolute docker build context (the Dockerfile lives here)
    Confidence Confidence,
    int? Port,                                   // port the container listens on, null for workers
    string? Version,                             // X.Y.Z the project already declares, if any
    IReadOnlyDictionary<string, string> Facts,   // stack-specific values the templates need
    IReadOnlyList<string> Notes);                // things the user should double-check

public record StackDetection(IReadOnlyList<DetectedComponent> Components, IReadOnlyList<string> Warnings);

public enum PlanItemKind { GitRepo, GitIgnore, GitAttributes, Dockerfile, DockerIgnore, VersionFile }

public enum PlanItemStatus { Exists, WillCreate }

/// <param name="Path">Absolute path.</param>
/// <param name="RelativePath">Relative to <see cref="SetupPlan.RepoRoot"/>, always with forward slashes.</param>
/// <param name="Content">Text that will be written; null for items that already exist and for GitRepo.</param>
public record PlanItem(
    string Id,
    PlanItemKind Kind,
    PlanItemStatus Status,
    string Path,
    string RelativePath,
    string Reason,
    string? Content,
    string? ComponentName);

public record SetupPlan(
    string RootPath,
    string RepoRoot,
    IReadOnlyList<DetectedComponent> Components,
    IReadOnlyList<PlanItem> Items,
    IReadOnlyList<string> Warnings,
    string? RepoBranch = null);   // branch the repo is on (or will start on); null when it cannot be read

public enum ApplyOutcome { Created, SkippedExists, SkippedNotApproved, Failed }

public record ApplyItemResult(string ItemId, ApplyOutcome Outcome, string Message);

public record ApplyOptions(IReadOnlyCollection<string> ApprovedItemIds, bool CommitCreatedFiles = false);

public record ApplyResult(IReadOnlyList<ApplyItemResult> Items, bool Committed, string? CommitError);
