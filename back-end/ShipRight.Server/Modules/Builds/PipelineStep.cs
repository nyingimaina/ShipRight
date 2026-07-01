namespace ShipRight.Modules.Builds;

/// <summary>
/// Canonical ordered registry of pipeline steps.
/// Derive step numbers from position — never hard-code them separately.
/// </summary>
public sealed record PipelineStep(string Name, int Number)
{
    public static readonly IReadOnlyList<PipelineStep> All =
    [
        new("PreconditionCheck",   1),
        new("GitStatusCheck",      2),
        new("BranchCheck",         3),
        new("WriteVersionsAndTag", 4),
        new("ComposeRepoSync",     5),
        new("DockerBuild",         6),
        new("BuildComplete",       7),
    ];

    public static int NumberOf(string name) =>
        All.FirstOrDefault(s => s.Name == name)?.Number
        ?? throw new InvalidOperationException($"Unknown pipeline step: '{name}'");
}
