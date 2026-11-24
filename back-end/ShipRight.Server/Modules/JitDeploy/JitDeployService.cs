using Serilog;
using ShipRight.Modules.Builds;
using ShipRight.Modules.Projects;
using ShipRight.Shared.Events;
using ShipRight.Shared.SshRunner;

namespace ShipRight.Modules.JitDeploy;

public sealed record JitDeployOptions(bool Force = false, string? Commit = null, bool AllowWithoutRollback = false, bool RebuildAll = false);

/// <summary>The record exists as soon as the deploy starts; <see cref="Completion"/> finishes when it is over.</summary>
public sealed record JitStartResult(BuildRecord? Record, string? Error, Task? Completion, bool NotFound = false);

public sealed record JitDependentsView(IReadOnlyList<DependentPair> Pairs, string? Note, bool NotFound = false);

public sealed record JitReleaseView(string Tag, string Sha, DateTime CreatedUtc, string? Message, bool IsCurrent, bool IsPrevious);

public sealed record JitReleasesView(
    string Phase, string? CurrentTag, string? PreviousTag, bool NeedsAttention, string? Attention,
    IReadOnlyList<JitReleaseView> Releases, string? Error);

/// <summary>
/// Runs build-on-server deploys for a project and records each one as an ordinary build record, so the existing
/// history and live log viewer show it with no changes. Never touches the other deploy modes.
/// </summary>
public sealed class JitDeployService(
    IProjectStore projects, IJitConfigStore configs, IBuildStore builds, BuildEventBus bus,
    ISshRunner ssh, IJitClock clock, JitDeployLock? locks = null, ShipRight.Modules.Resources.Stores.ICredentialResourceStore? credentials = null,
    JitLocalBranchGuard? localBranchGuard = null, Modules.Servers.IServerStore? servers = null)
{
    private readonly JitDeployer _deployer = new(ssh, clock, locks);

    public Task<JitStartResult> StartDeployAsync(string projectId, JitDeployOptions? options, string? initiator) =>
        StartAsync(projectId, initiator, rollbackTag: null, isRollback: false, buildingFromSource: true, options ?? new JitDeployOptions(),
            (req, log, ct) => _deployer.DeployAsync(req, log, ct));

    public Task<JitStartResult> StartRecoverAsync(string projectId, string? initiator) =>
        StartAsync(projectId, initiator, rollbackTag: null, isRollback: false, buildingFromSource: false, new JitDeployOptions(),
            (req, log, ct) => _deployer.RecoverAsync(req, log, ct));

    /// <summary>Stops all of the project's services and starts them again on the release that is already running.</summary>
    public Task<JitStartResult> StartRestartAllAsync(string projectId, string? initiator) =>
        StartAsync(projectId, initiator, rollbackTag: null, isRollback: false, buildingFromSource: false, new JitDeployOptions(),
            (req, log, ct) => _deployer.RestartAllAsync(req, log, ct));

    public async Task<JitStartResult> StartRollbackAsync(string projectId, string tag, string? initiator)
    {
        if (!ReleaseTag.TryParse(tag, out _, out _))
            return new(null, $"'{tag}' is not a release tag.", null);
        return await StartAsync(projectId, initiator, tag, isRollback: true, buildingFromSource: false, new JitDeployOptions(),
            (req, log, ct) => _deployer.RollbackToAsync(req, tag, log, ct));
    }

    private async Task<JitStartResult> StartAsync(
        string projectId, string? initiator, string? rollbackTag, bool isRollback, bool buildingFromSource, JitDeployOptions options,
        Func<JitDeployRequest, Func<string, Task>, CancellationToken, Task<JitDeployResult>> action)
    {
        var project = await projects.GetByIdAsync(projectId);
        if (project is null) return new(null, $"Project '{projectId}' not found.", null, NotFound: true);

        JitDeployConfig? cfg;
        try { cfg = await configs.GetAsync(projectId); }
        catch (InvalidDataException ex) { return new(null, ex.Message, null); }

        // The server record owns the server-level variables. The project's embedded copy can be stale, so prefer the
        // record whenever one exists. Older callers with no server store keep the original behaviour.
        var serverEnv = project.Server.EnvFile;
        if (servers is not null)
        {
            var serverId = string.IsNullOrWhiteSpace(project.Server.Id) ? project.ServerId : project.Server.Id;
            if (!string.IsNullOrWhiteSpace(serverId) && await servers.GetByIdAsync(serverId) is { } serverRecord)
                serverEnv = serverRecord.EnvFile;
        }

        var map = JitEnvMapper.MapWithServerEnv(project, cfg, serverEnv, options.Commit, options.Force, options.AllowWithoutRollback);
        if (!map.Ok) return new(null, JitServiceHelp.StartMessage(map.Problems), null);

        // A source build takes origin/branch from the server. Refuse it here, before a build record or any SSH work,
        // if the matching local checkout still has uncommitted or unpushed changes. An explicit commit builds that
        // SHA rather than "latest", so it is not subject to the branch check.
        if (buildingFromSource && localBranchGuard is not null && string.IsNullOrWhiteSpace(options.Commit) && cfg is not null)
        {
            var local = await localBranchGuard.CheckAsync(project, cfg);
            if (local.Problem is not null) return new(null, local.Problem, null);
        }

        var (withCredential, credentialError) = await JitCredentialResolver.ApplyAsync(map.Request!, cfg, credentials, projectId);
        if (credentialError is not null) return new(null, credentialError, null);
        var request = withCredential! with { RebuildAll = options.RebuildAll };

        var record = new BuildRecord
        {
            ProjectId = project.Id,
            ProjectName = project.Name,
            Status = BuildStatus.Deploying,
            Initiator = initiator,
            IsRollback = isRollback,
            GitTag = rollbackTag ?? string.Empty,
            Versions = request.Services.Select(s => new ServiceVersion
            {
                ServiceName = s.Name, DockerImageName = s.ImageRef, NewVersion = rollbackTag ?? string.Empty,
            }).ToList(),
        };
        await builds.SaveAsync(record);
        bus.Register(record.Id);

        var completion = Task.Run(() => ExecuteAsync(record, request, action));
        return new(record, null, completion);
    }

    private async Task ExecuteAsync(
        BuildRecord record, JitDeployRequest request,
        Func<JitDeployRequest, Func<string, Task>, CancellationToken, Task<JitDeployResult>> action)
    {
        var ctx = new PipelineContext(record, bus);
        var started = DateTime.UtcNow;
        try
        {
            await ctx.EmitLogAsync($"Connecting to {request.Target.Username}@{request.Target.Host}…", "ssh");
            var result = await action(request, line => ctx.EmitLogAsync(line, "deploy"), CancellationToken.None);
            Apply(record, result);
            LogOutcome(record, result);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Build-on-server deploy {BuildId} crashed", record.Id);
            record.Status = BuildStatus.DeployFailed;
            record.ErrorSummary = $"The deploy stopped unexpectedly: {ex.Message}. Check the server; run recover to be safe.";
            await ctx.EmitLogAsync($"[ERROR] {record.ErrorSummary}", "deploy");
        }

        record.CompletedAt = DateTime.UtcNow;
        record.StepDurations["Deploy"] = (int)(DateTime.UtcNow - started).TotalSeconds;
        record.AppendLogLine($"[Deployment finished: {record.Status}]");
        await builds.SaveAsync(record);
        await ctx.DeployCompletedAsync();
    }

    /// <summary>Persists why a deploy failed, not just that it did, so the server log is useful without the UI.</summary>
    private static void LogOutcome(BuildRecord record, JitDeployResult result)
    {
        if (result.Success)
            Log.Information("Build-on-server deploy {BuildId} for {ProjectId} finished: {Outcome}", record.Id, record.ProjectId, result.Outcome);
        else
            Log.Warning("Build-on-server deploy {BuildId} for {ProjectId} failed: {Outcome} — {Reason}",
                record.Id, record.ProjectId, result.Outcome, result.Message);
    }

    private static void Apply(BuildRecord record, JitDeployResult result)
    {
        if (result.Success)
        {
            record.Status = BuildStatus.Deployed;
            record.ErrorSummary = null;
            if (result.Outcome == JitOutcome.Deployed)
            {
                record.DeployedAt = DateTime.UtcNow;
                if (result.Tag is not null)
                {
                    record.GitTag = result.Tag;
                    for (var i = 0; i < record.Versions.Count; i++)
                        record.Versions[i] = record.Versions[i] with { NewVersion = result.Tag };
                }
            }
            record.AppendLogLine(result.Message);
        }
        else
        {
            record.Status = BuildStatus.DeployFailed;
            record.ErrorSummary = result.Message;
            record.AppendLogLine("[ERROR] " + result.Message);
        }
    }

    // ── the restart questions ────────────────────────────────────────────────

    /// <summary>
    /// Reads the compose file already on the server (read-only: nothing is checked out, built or stopped) and lists the
    /// "also restart this one?" questions, with the answers saved so far. Empty, with a note, before the first deploy.
    /// </summary>
    public async Task<JitDependentsView> GetDependentsAsync(string projectId)
    {
        var project = await projects.GetByIdAsync(projectId);
        if (project is null) return new([], $"Project '{projectId}' not found.", NotFound: true);

        var target = JitDeployMapper.TargetOf(project);
        if (target is null) return new([], "Choose the server this project deploys to first.");

        try
        {
            var cfg = await configs.GetAsync(projectId) ?? new JitDeployConfig();
            var services = JitDeployMapper.Services(project, cfg);
            var ctx = new ComposeContext(target, JitDeployMapper.ComposeProjectName(project, cfg), string.IsNullOrWhiteSpace(cfg.EnvFile) ? null : cfg.EnvFile,
                string.IsNullOrWhiteSpace(cfg.ComposeFile) ? null : cfg.ComposeFile.Trim());
            var bindings = services.Select(s => new TagBinding(s.TagVariable, s.ImageRef)).ToList();

            var lines = new List<string>();
            var exit = await ssh.RunAsync(target.Host, target.Username, target.SshKeyPath,
                JitCommands.ComposeConfig(ctx, bindings, "r1-0000000"),
                l => { lock (lines) lines.Add(l); return Task.CompletedTask; }, _ => Task.CompletedTask);
            if (exit != 0)
                return new([], "There is no compose file on the server yet. Deploy once so it can be read, and the questions appear here.");

            var graph = ComposeConfig.Graph(ComposeConfig.Parse(string.Join("\n", lines)));
            var choices = (cfg.DependentChoices ?? []).GroupBy(c => (c.Service, c.Dependent)).ToDictionary(g => g.Key, g => g.Last().Restart);
            var pairs = DependentPairs.Compute(graph, services.Select(s => s.Name).Where(graph.ContainsKey), choices);
            return new(pairs, null);
        }
        catch (ComposeConfigException ex) { return new([], ex.Message); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return new([], $"Could not read the compose file from the server: {ex.Message}"); }
    }

    // ── what is on the server ────────────────────────────────────────────────

    public async Task<JitReleasesView> GetReleasesAsync(string projectId)
    {
        JitReleasesView Fail(string error) => new("Unknown", null, null, false, null, [], error);

        var project = await projects.GetByIdAsync(projectId);
        if (project is null) return Fail($"Project '{projectId}' not found.");
        var target = JitDeployMapper.TargetOf(project);
        if (target is null) return Fail("Choose the server this project deploys to first.");

        ReleaseState state;
        try { state = await new ReleaseStateStore(ssh).ReadAsync(target); }
        catch (StateCorruptException ex) { return Fail(ex.Message); }
        catch (Exception ex) { return Fail($"Could not reach the server: {ex.Message}"); }

        var recovery = ReleaseStateMachine.Recovery(state.Phase);
        string? attention = recovery switch
        {
            RecoveryAction.None => null,
            RecoveryAction.Manual => "A rollback failed earlier and the server needs attention. Fix the cause, then run recover to retry it.",
            RecoveryAction.Rollback => "A deploy was interrupted while it was changing the server. Run recover to undo it safely.",
            _ => "A deploy was interrupted. Run recover to clean it up.",
        };

        var releases = state.Releases
            .Select(r => { ReleaseTag.TryParse(r.Tag, out var n, out _); return (r, n); })
            .OrderByDescending(x => x.n)
            .Select(x => new JitReleaseView(x.r.Tag, x.r.Sha, x.r.CreatedUtc, x.r.Message, x.r.Tag == state.CurrentTag, x.r.Tag == state.PreviousTag))
            .ToList();

        return new(state.Phase.ToString(), state.CurrentTag, state.PreviousTag, recovery != RecoveryAction.None, attention, releases, null);
    }
}
