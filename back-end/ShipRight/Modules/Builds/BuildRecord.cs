using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace ShipRight.Modules.Builds;

public record ServiceVersion
{
    public string ServiceName { get; init; } = string.Empty;
    public string PreviousVersion { get; init; } = string.Empty;
    public string NewVersion { get; init; } = string.Empty;
    public string DockerImageName { get; init; } = string.Empty;
}

[JsonConverter(typeof(StringEnumConverter))]
public enum BuildStatus
{
    Pending,
    Running,
    Paused,
    ImageBuilt,
    PushSucceeded,
    PushFailed,
    BuildFailed,
    Aborted,
    Interrupted,
    Deploying,
    Deployed,
    DeployFailed
}

public class BuildRecord
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string ProjectId { get; init; } = string.Empty;
    public string ProjectName { get; init; } = string.Empty;
    public BuildStatus Status { get; set; } = BuildStatus.Pending;
    public string GitTag { get; set; } = string.Empty;
    public List<ServiceVersion> Versions { get; init; } = new();
    public DateTime StartedAt { get; init; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public DateTime? DeployedAt { get; set; }
    public string? FailedStep { get; set; }
    public int? CurrentStepNumber { get; set; }
    public string? CurrentStepName { get; set; }
    public string LogOutput { get; set; } = string.Empty;
    public string? ErrorSummary { get; set; }
    public Dictionary<string, int> StepDurations { get; set; } = new();
    public List<string> SucceededSteps { get; set; } = new();
}
