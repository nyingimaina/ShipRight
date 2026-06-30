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
    public string? ErrorSummary { get; set; }
    public Dictionary<string, int> StepDurations { get; set; } = new();
    public List<string> SucceededSteps { get; set; } = new();
    public bool IsRollback { get; set; } = false;
    public string? RolledBackFromBuildId { get; set; }

    // ── Log storage (O(1) append) ─────────────────────────────────────────────
    // Private backing store — never exposed as a collection so callers
    // cannot mutate it except through AppendLogLine.
    [JsonIgnore]
    private readonly List<string> _logLines = new();

    /// <summary>O(1) append — use this instead of LogOutput +=.</summary>
    internal void AppendLogLine(string line) => _logLines.Add(line);

    /// <summary>Materialize the full log on demand (read path only).</summary>
    public string GetLogOutput() =>
        _logLines.Count == 0 ? string.Empty : string.Join('\n', _logLines);

    /// <summary>
    /// JSON serialization property — kept for on-disk compatibility with
    /// existing build records. Reads/writes the full log as a single string.
    /// New code should call AppendLogLine / GetLogOutput instead.
    /// </summary>
    public string LogOutput
    {
        get => GetLogOutput();
        set
        {
            _logLines.Clear();
            if (!string.IsNullOrEmpty(value))
                _logLines.AddRange(value.Split('\n'));
        }
    }
}
