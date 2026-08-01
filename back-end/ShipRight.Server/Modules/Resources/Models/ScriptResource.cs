namespace ShipRight.Modules.Resources.Models;

public record ScriptResource
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public ScriptPlatform Platform { get; init; } = ScriptPlatform.Bash;
    public ExecutionTarget Target { get; init; } = ExecutionTarget.Local;
    public PipelineScope Scope { get; init; } = PipelineScope.Global;
    public string? ProjectId { get; init; }
    public string? WorkingDirectory { get; init; }
    public Dictionary<string, string> Variables { get; init; } = [];
    public DateTime CreatedAt { get; init; }
    public DateTime ModifiedAt { get; set; }
}
