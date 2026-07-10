using ShipRight.Modules.Projects;

namespace ShipRight.Modules.Resources.Models;

public record PipelineResource
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = string.Empty;
    public List<PipelineStep> Steps { get; init; } = [];
    public PipelineScope Scope { get; init; } = PipelineScope.Global;
    public Guid? ProjectId { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime ModifiedAt { get; set; }
}

public record PipelineStep
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public PipelineStepType Type { get; init; }
    public Guid? ScriptResourceId { get; init; }
    public DeployMode? DeployMode { get; init; }
    public string? Label { get; init; }
    public bool ContinueOnError { get; init; } = false;
}
