using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShipRight.Modules.JitDeploy;

/// <summary>
/// Where a deploy is. <c>OldStopped</c> is entered BEFORE the first old container is stopped, so it means "the old
/// release may be partly or fully stopped": from then on a failure must roll back.
/// </summary>
public enum DeployPhase { Idle, Syncing, Building, OldStopped, NewStarted, Verified, FailedBuild, RollingBack, RolledBack, RollbackFailed }

/// <summary>What to do with a state found on disk when no deploy is running (the deployer crashed or was restarted).</summary>
public enum RecoveryAction { None, Reset, Rollback, Commit, Manual }

public sealed class InvalidTransitionException(DeployPhase from, DeployPhase to)
    : Exception($"A deploy cannot go from {from} to {to}.");

public sealed class StateCorruptException(string detail)
    : Exception($"The saved deploy state could not be used: {detail}");

public static class ReleaseStateMachine
{
    private static readonly Dictionary<DeployPhase, DeployPhase[]> Allowed = new()
    {
        [DeployPhase.Idle] = [DeployPhase.Syncing],
        [DeployPhase.Syncing] = [DeployPhase.Building, DeployPhase.Idle],
        [DeployPhase.Building] = [DeployPhase.OldStopped, DeployPhase.FailedBuild],
        [DeployPhase.FailedBuild] = [DeployPhase.Idle],
        [DeployPhase.OldStopped] = [DeployPhase.NewStarted, DeployPhase.RollingBack],
        [DeployPhase.NewStarted] = [DeployPhase.Verified, DeployPhase.RollingBack],
        [DeployPhase.Verified] = [DeployPhase.Idle],
        [DeployPhase.RollingBack] = [DeployPhase.RolledBack, DeployPhase.RollbackFailed],
        [DeployPhase.RolledBack] = [DeployPhase.Idle],
        [DeployPhase.RollbackFailed] = [DeployPhase.RollingBack, DeployPhase.Idle],
    };

    public static bool CanMove(DeployPhase from, DeployPhase to) => Allowed[from].Contains(to);

    public static RecoveryAction Recovery(DeployPhase phase) => phase switch
    {
        DeployPhase.Idle => RecoveryAction.None,
        DeployPhase.Syncing or DeployPhase.Building or DeployPhase.FailedBuild or DeployPhase.RolledBack => RecoveryAction.Reset,
        DeployPhase.OldStopped or DeployPhase.NewStarted or DeployPhase.RollingBack => RecoveryAction.Rollback,
        DeployPhase.Verified => RecoveryAction.Commit,
        DeployPhase.RollbackFailed => RecoveryAction.Manual,
        _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, "Unknown deploy phase."),
    };
}

/// <summary>One finished release on the server: its tag, the commit it was built from and the images built for it.</summary>
public sealed record ReleaseRecord(string Tag, string Sha, IReadOnlyList<string> Images, DateTime CreatedUtc, string? Message = null);

public sealed record ReleaseState
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;
    public DeployPhase Phase { get; init; } = DeployPhase.Idle;
    public string? CurrentTag { get; init; }
    public string? PreviousTag { get; init; }
    public string? InProgressTag { get; init; }
    /// <summary>Services being restarted by the deploy in progress, in start order. Lets a crashed deploy be rolled back without the original request's graph.</summary>
    public IReadOnlyList<string> InProgressAffected { get; init; } = [];
    /// <summary>True while an on-purpose rollback to an existing release is in progress: that release's images and record must survive a failure.</summary>
    public bool InProgressReusesRelease { get; init; }
    public IReadOnlyList<ReleaseRecord> Releases { get; init; } = [];
    public DateTime UpdatedUtc { get; init; }

    public static ReleaseState Empty => new();

    public ReleaseState MoveTo(DeployPhase next, DateTime nowUtc)
    {
        if (!ReleaseStateMachine.CanMove(Phase, next)) throw new InvalidTransitionException(Phase, next);
        return this with { Phase = next, UpdatedUtc = nowUtc };
    }
}

/// <summary>Plain, indented, camelCase JSON that a person can read and fix by hand on the server.</summary>
public static class ReleaseStateJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        NewLine = "\n",
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string Serialize(ReleaseState state) => JsonSerializer.Serialize(state, Options);

    public static ReleaseState Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) throw new StateCorruptException("the state file is empty.");
        ReleaseState? state;
        try { state = JsonSerializer.Deserialize<ReleaseState>(json, Options); }
        catch (JsonException ex) { throw new StateCorruptException($"it is not valid state data ({ex.Message})"); }
        if (state is null) throw new StateCorruptException("it contains no state.");
        if (state.Version > ReleaseState.CurrentVersion)
            throw new StateCorruptException($"it was written by a newer version (format {state.Version}); refusing to overwrite it.");
        return state;
    }
}
