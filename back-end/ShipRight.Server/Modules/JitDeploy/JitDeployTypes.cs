namespace ShipRight.Modules.JitDeploy;

/// <summary>A service the deploy builds: its compose name, the compose variable that carries its image tag, and its image repository.</summary>
public sealed record JitService(string Name, string TagVariable, string ImageRef);

/// <summary>Every value here is a per-project setting with a sensible default.</summary>
public sealed record JitDeploySettings
{
    public int ReleasesToKeep { get; init; } = 3;
    public int StopGraceSeconds { get; init; } = 30;
    public int HealthTimeoutSeconds { get; init; } = 60;
    public int SettleSeconds { get; init; } = 10;
    public int MinFreeDiskMb { get; init; } = 3000;
    public int PollSeconds { get; init; } = 2;
    /// <summary>Optional build priority (0-19) so a build does not starve running containers.</summary>
    public int? BuildNice { get; init; }
}

public sealed record JitDeployRequest
{
    public required JitTarget Target { get; init; }
    public required string Project { get; init; }
    public required string RepoUrl { get; init; }
    public required IReadOnlyList<JitService> Services { get; init; }
    public string Branch { get; init; } = "master";
    /// <summary>Deploy this exact commit instead of the branch head.</summary>
    public string? Commit { get; init; }
    public string? EnvFile { get; init; }
    public JitDeploySettings Settings { get; init; } = new();
    /// <summary>(service, dependent) → restart the dependent when the service is replaced. Unanswered pairs restart.</summary>
    public IReadOnlyDictionary<(string Service, string Dependent), bool>? DependentChoices { get; init; }
    /// <summary>Deploy even when the branch has not moved since the current release.</summary>
    public bool Force { get; init; }
    /// <summary>Go ahead even though the previous release's images are missing, so a rollback would not be possible.</summary>
    public bool AllowWithoutRollback { get; init; }
}

public enum JitOutcome
{
    Deployed,
    NothingToDeploy,
    /// <summary>Preflight, sync, validation or build failed (or was cancelled): the running release was never touched.</summary>
    FailedBeforeChange,
    /// <summary>The new release failed after the old one was stopped; the previous release is running again.</summary>
    RolledBack,
    /// <summary>The new release failed and the previous one could not be brought back either. Needs a person.</summary>
    RollbackFailed,
    /// <summary>The first release on this server failed: what had started was stopped, there was nothing to restore.</summary>
    FailedNoPreviousRelease,
    /// <summary>The saved state needs attention (a failed rollback or an unreadable file); nothing was done.</summary>
    Blocked,
    /// <summary>Another deploy of this project is running.</summary>
    Busy,
    /// <summary>The connection dropped mid-deploy. The saved state on the server says where it got to; recover to finish safely.</summary>
    ConnectionLost,
}

public sealed record JitDeployResult(JitOutcome Outcome, string Message, string? Tag = null, string? Sha = null, string? FailedService = null)
{
    public bool Success => Outcome is JitOutcome.Deployed or JitOutcome.NothingToDeploy;
}

/// <summary>Time and waiting, so the health gate can be tested without really waiting.</summary>
public interface IJitClock
{
    DateTime UtcNow { get; }
    Task DelayAsync(TimeSpan delay, CancellationToken ct);
}

public sealed class SystemJitClock : IJitClock
{
    public DateTime UtcNow => DateTime.UtcNow;
    public Task DelayAsync(TimeSpan delay, CancellationToken ct) => Task.Delay(delay, ct);
}

/// <summary>One deploy at a time per project and server. Held for the whole deploy, released even on failure.</summary>
public sealed class JitDeployLock
{
    public static JitDeployLock Shared { get; } = new();

    private readonly HashSet<string> _held = new(StringComparer.Ordinal);

    public static string KeyFor(JitTarget t) => $"{t.Host}|{t.ProjectDir}";

    public IDisposable? TryAcquire(string key)
    {
        lock (_held) { if (!_held.Add(key)) return null; }
        return new Releaser(this, key);
    }

    private sealed class Releaser(JitDeployLock owner, string key) : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) lock (owner._held) owner._held.Remove(key);
        }
    }
}
