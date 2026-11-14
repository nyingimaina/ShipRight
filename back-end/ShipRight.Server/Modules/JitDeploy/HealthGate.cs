namespace ShipRight.Modules.JitDeploy;

public enum ContainerState { Created, Running, Restarting, Paused, Exited, Dead }

public enum HealthStatus { None, Starting, Healthy, Unhealthy }

public enum GateVerdict { Pass, Wait, Fail }

/// <summary>What was observed about one container since it was started.</summary>
public sealed record GateObservation(
    ContainerState State, HealthStatus Health, int RestartCount, int RestartCountAtStart, TimeSpan Elapsed);

public sealed record GateOptions(TimeSpan HealthTimeout, TimeSpan SettleTime)
{
    public GateOptions() : this(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(10)) { }
}

public sealed record GateDecision(GateVerdict Verdict, string Reason);

/// <summary>
/// Decides whether a freshly started container is good (pass), not known yet (wait) or bad (fail). A container
/// fails if it exited, reports unhealthy, is restarting, has restarted since it was started, or has not become
/// healthy in time. Without a health check it passes once it has stayed up for the settle time.
/// </summary>
public static class HealthGate
{
    public static GateDecision Decide(GateObservation o, GateOptions options)
    {
        if (o.RestartCount > o.RestartCountAtStart)
            return Fail($"it restarted {o.RestartCount - o.RestartCountAtStart} time(s) since it was started (restart loop)");
        if (o.State == ContainerState.Restarting)
            return Fail("it keeps restarting (restart loop)");
        if (o.State is ContainerState.Exited or ContainerState.Dead)
            return Fail($"the container exited (state: {o.State.ToString().ToLowerInvariant()})");
        if (o.Health == HealthStatus.Unhealthy)
            return Fail("its health check reports unhealthy");

        var timedOut = o.Elapsed >= options.HealthTimeout;
        var seconds = (int)options.HealthTimeout.TotalSeconds;

        if (o.State == ContainerState.Running)
        {
            switch (o.Health)
            {
                case HealthStatus.Healthy:
                    return new(GateVerdict.Pass, "healthy");
                case HealthStatus.Starting:
                    return timedOut
                        ? Fail($"it did not become healthy within {seconds} seconds")
                        : new(GateVerdict.Wait, "waiting for its health check");
                default:
                    return o.Elapsed >= options.SettleTime
                        ? new(GateVerdict.Pass, $"running steadily for {(int)options.SettleTime.TotalSeconds} seconds (no health check defined)")
                        : new(GateVerdict.Wait, "waiting to see that it stays up");
            }
        }

        // created / paused: not running yet.
        return timedOut
            ? Fail($"it was not running within {seconds} seconds (state: {o.State.ToString().ToLowerInvariant()})")
            : new(GateVerdict.Wait, "waiting for it to start");
    }

    public static ContainerState ParseState(string raw) => (raw ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "running" => ContainerState.Running,
        "exited" => ContainerState.Exited,
        "restarting" => ContainerState.Restarting,
        "created" => ContainerState.Created,
        "paused" => ContainerState.Paused,
        "dead" or "removing" => ContainerState.Dead,
        var other => throw new ArgumentException($"Unknown container state '{other}'.", nameof(raw)),
    };

    /// <summary>A container with no health check reports nothing (or "&lt;no value&gt;"); that is <see cref="HealthStatus.None"/>.</summary>
    public static HealthStatus ParseHealth(string raw) => (raw ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "" or "none" or "<no value>" => HealthStatus.None,
        "healthy" => HealthStatus.Healthy,
        "unhealthy" => HealthStatus.Unhealthy,
        "starting" => HealthStatus.Starting,
        var other => throw new ArgumentException($"Unknown health status '{other}'.", nameof(raw)),
    };

    private static GateDecision Fail(string reason) => new(GateVerdict.Fail, reason);
}
