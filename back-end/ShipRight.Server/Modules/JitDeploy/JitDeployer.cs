using ShipRight.Shared.SshRunner;

namespace ShipRight.Modules.JitDeploy;

/// <summary>
/// Deploys a project by building its images on the server it runs on, all or nothing:
/// preflight, sync to one exact commit, build every image, and only then stop the old services (dependents first),
/// start the new ones (dependencies first) and health-check each. A failure before the old release is touched changes
/// nothing. A failure after it rolls back at once. A rollback that fails is retried once, then reported with the
/// commands to fix it by hand. Where the deploy is, is saved on the server before every step, so an interrupted
/// deploy can be recovered. One deploy at a time per project.
/// </summary>
public sealed class JitDeployer(ISshRunner ssh, IJitClock clock, JitDeployLock? locks = null)
{
    private readonly JitDeployLock _locks = locks ?? JitDeployLock.Shared;

    public async Task<JitDeployResult> DeployAsync(JitDeployRequest request, Func<string, Task> onLine, CancellationToken ct = default) =>
        Scrub(await DeployScrubbedAsync(request, Scrubbed(onLine, request), ct), request);

    private async Task<JitDeployResult> DeployScrubbedAsync(JitDeployRequest request, Func<string, Task> onLine, CancellationToken ct)
    {
        try { Validate(request); }
        catch (ArgumentException ex) { return new(JitOutcome.FailedBeforeChange, $"The deploy settings are not valid: {ex.Message}"); }

        using var held = _locks.TryAcquire(JitDeployLock.KeyFor(request.Target));
        if (held is null)
            return new(JitOutcome.Busy, "Another deploy of this project is already running. Wait for it to finish.");

        return await new Run(ssh, clock, request, onLine).DeployAsync(ct);
    }

    /// <summary>Finishes or undoes whatever a crashed or failed deploy left behind. Safe to run on a healthy server.</summary>
    public async Task<JitDeployResult> RecoverAsync(JitDeployRequest request, Func<string, Task> onLine, CancellationToken ct = default) =>
        Scrub(await RecoverScrubbedAsync(request, Scrubbed(onLine, request), ct), request);

    private async Task<JitDeployResult> RecoverScrubbedAsync(JitDeployRequest request, Func<string, Task> onLine, CancellationToken ct)
    {
        try { Validate(request); }
        catch (ArgumentException ex) { return new(JitOutcome.FailedBeforeChange, $"The deploy settings are not valid: {ex.Message}"); }

        using var held = _locks.TryAcquire(JitDeployLock.KeyFor(request.Target));
        if (held is null)
            return new(JitOutcome.Busy, "A deploy of this project is running, so there is nothing to recover right now.");

        return await new Run(ssh, clock, request, onLine).RecoverAsync(ct);
    }

    /// <summary>
    /// Goes back to a release still kept on the server, on purpose: the same all-or-nothing swap as a deploy, with no
    /// build. If it fails, the release that was running is put back and the one you tried is kept.
    /// </summary>
    public async Task<JitDeployResult> RollbackToAsync(JitDeployRequest request, string targetTag, Func<string, Task> onLine, CancellationToken ct = default) =>
        Scrub(await RollbackToScrubbedAsync(request, targetTag, Scrubbed(onLine, request), ct), request);

    /// <summary>Whatever a server or a library says can contain a password or token; none of it may reach the log or the history.</summary>
    private static Func<string, Task> Scrubbed(Func<string, Task> sink, JitDeployRequest request) =>
        line => sink(GitAddressForms.Redact(line, SecretsOf(request)));

    private static JitDeployResult Scrub(JitDeployResult r, JitDeployRequest request) =>
        r with { Message = GitAddressForms.Redact(r.Message, SecretsOf(request)) };

    /// <summary>Anything the deploy was given that must never be shown, whatever shape it has.</summary>
    private static IReadOnlyCollection<string> SecretsOf(JitDeployRequest request) =>
        request.GitCredential is { } g ? [g.Token] : [];

    private async Task<JitDeployResult> RollbackToScrubbedAsync(JitDeployRequest request, string targetTag, Func<string, Task> onLine, CancellationToken ct)
    {
        try { Validate(request); }
        catch (ArgumentException ex) { return new(JitOutcome.FailedBeforeChange, $"The deploy settings are not valid: {ex.Message}"); }
        if (!ReleaseTag.TryParse(targetTag, out _, out _))
            return new(JitOutcome.FailedBeforeChange, $"'{targetTag}' is not a release tag.");

        using var held = _locks.TryAcquire(JitDeployLock.KeyFor(request.Target));
        if (held is null)
            return new(JitOutcome.Busy, "Another deploy of this project is already running. Wait for it to finish.");

        return await new Run(ssh, clock, request, onLine).RollbackToAsync(targetTag, ct);
    }

    /// <summary>Builds every command once, so any unsafe or malformed value is refused before a single byte is sent.</summary>
    private static void Validate(JitDeployRequest r)
    {
        if (r.Services.Count == 0) throw new ArgumentException("There are no services to deploy.");
        var s = r.Settings;
        if (s.StopGraceSeconds < 0 || s.HealthTimeoutSeconds <= 0 || s.SettleSeconds < 0 || s.MinFreeDiskMb <= 0 || s.PollSeconds <= 0 || s.ReleasesToKeep < 0)
            throw new ArgumentException("A timeout or count in the settings is out of range.");
        if (r.Commit is null) JitNames.Branch(r.Branch); else JitNames.Sha(r.Commit);

        if (r.Services.Any(s => s.Recipe is not null))
        {
            if (r.ComposeFile is null) throw new ArgumentException("Pick the compose file so ShipRight can add the build instructions to it.");
            ComposeOverride.Render(r.ComposeFile, r.Services);
        }
        var ctx = new ComposeContext(r.Target, r.Project, r.EnvFile, r.ComposeFile, OverrideFor(r));
        var bindings = Bind(r.Services);
        const string dummy = "r1-0000000";
        JitCommands.CheckDisk(r.Target, s.MinFreeDiskMb);
        JitCommands.CheckGit(r.RepoUrl);
        JitCommands.SyncRepo(r.Target, r.RepoUrl);
        if (r.DeployKeyName is { } deployKey) JitDeployKey.GitSshPrefix(deployKey);
        if (r.GitCredential is { } credential)
        {
            if (r.DeployKeyName is not null) throw new ArgumentException("Choose either a deploy key or a saved credential, not both.");
            JitGitCredential.Wrap("true", credential);
        }
        JitCommands.Build(ctx, r.Services.Select(x => x.Name).ToList(), bindings, dummy, s.BuildNice);
        JitCommands.StopService(ctx, r.Services[0].Name, s.StopGraceSeconds, bindings, dummy);
        JitCommands.RemoveImages(r.Services.Select(x => $"{x.ImageRef}:{dummy}"));
    }

    /// <summary>Where the generated build instructions live, when any service has a recipe.</summary>
    private static string? OverrideFor(JitDeployRequest r) => r.Services.Any(s => s.Recipe is not null) ? ComposeOverride.Path(r.Target) : null;

    private static IReadOnlyList<TagBinding> Bind(IEnumerable<JitService> services) =>
        services.Select(s => new TagBinding(s.TagVariable, s.ImageRef)).ToList();

    // ═════════════════════════════════════════════════════════════════════════

    private sealed class Run
    {
        private readonly ISshRunner _ssh;
        private readonly IJitClock _clock;
        private readonly JitDeployRequest _req;
        private readonly Func<string, Task> _log;
        private readonly JitTarget _t;
        private readonly ComposeContext _ctx;
        private readonly IReadOnlyList<TagBinding> _bindings;
        private readonly ReleaseStateStore _store;
        private readonly JitDeploySettings _s;
        private ReleaseState _state = ReleaseState.Empty;

        public Run(ISshRunner ssh, IJitClock clock, JitDeployRequest req, Func<string, Task> log)
        {
            _ssh = ssh; _clock = clock; _req = req; _log = log;
            _t = req.Target;
            _ctx = new ComposeContext(req.Target, req.Project, req.EnvFile, req.ComposeFile, OverrideFor(req));
            _bindings = Bind(req.Services);
            _store = new ReleaseStateStore(ssh);
            _s = req.Settings;
        }

        private Task Say(string message) => _log("[deploy] " + message);

        /// <summary>Commands that reach the git host use the deploy key when one is set; nothing else is touched.</summary>
        private string WithKey(string command) =>
            _req.GitCredential is { } secret ? JitGitCredential.Wrap(command, secret)
            : _req.DeployKeyName is { } key ? JitDeployKey.GitSshPrefix(key) + command
            : command;

        private static JitDeployResult Failed(string message) => new(JitOutcome.FailedBeforeChange, message);

        // ── plumbing ─────────────────────────────────────────────────────────

        private async Task<(int Exit, List<string> Lines)> Exec(string command, bool forward, CancellationToken ct, bool mergeStderr = false)
        {
            var lines = new List<string>();
            Task Out(string l) { lock (lines) lines.Add(l); return forward ? _log(l) : Task.CompletedTask; }
            Task Err(string l) => mergeStderr ? Out(l) : (forward ? _log(l) : Task.CompletedTask);
            var exit = await _ssh.RunAsync(_t.Host, _t.Username, _t.SshKeyPath, command, Out, Err, ct);
            lock (lines) return (exit, lines.ToList());
        }

        private async Task Save(ReleaseState next, CancellationToken ct = default)
        {
            await _store.WriteAsync(_t, next with { UpdatedUtc = _clock.UtcNow }, ct);
            _state = next with { UpdatedUtc = _clock.UtcNow };
        }

        private Task Move(DeployPhase to, Func<ReleaseState, ReleaseState>? change = null, CancellationToken ct = default)
        {
            var next = _state.MoveTo(to, _clock.UtcNow);
            return Save(change is null ? next : change(next), ct);
        }

        private static string Tail(IEnumerable<string> lines, int n = 3) =>
            string.Join(" | ", lines.Select(l => l.Trim()).Where(l => l.Length > 0).TakeLast(n));

        private static string PreflightMessage(IEnumerable<string> lines, string fallback)
        {
            var p = lines.Where(l => l.StartsWith("[preflight]", StringComparison.Ordinal)).Select(l => l["[preflight]".Length..].Trim()).ToList();
            return p.Count > 0 ? string.Join(" ", p) : fallback;
        }

        private string ExpectedRef(JitService s, string tag) => $"{s.ImageRef}:{tag}";
        private IReadOnlyList<string> ExpectedRefs(string tag) => _req.Services.Select(s => ExpectedRef(s, tag)).ToList();

        // ── entry points ─────────────────────────────────────────────────────

        public async Task<JitDeployResult> DeployAsync(CancellationToken ct)
        {
            try
            {
                _state = await _store.ReadAsync(_t, ct);

                switch (ReleaseStateMachine.Recovery(_state.Phase))
                {
                    case RecoveryAction.None: break;
                    case RecoveryAction.Reset: await ResetAsync(); break;
                    case RecoveryAction.Commit:
                        await Say($"A previous deploy had finished verifying release {_state.InProgressTag} but was not recorded; recording it now.");
                        await CommitAsync(CancellationToken.None);
                        break;
                    case RecoveryAction.Rollback:
                    {
                        await Say("A previous deploy was interrupted. Undoing it before anything new is deployed.");
                        var r = await RollbackAsync(null, "the deploy was interrupted");
                        return r with { Message = "A previous deploy was interrupted, so it was undone first. " + r.Message + " Deploy again to continue." };
                    }
                    default:
                        return new(JitOutcome.Blocked, BlockedMessage());
                }

                return await DeployCoreAsync(ct);
            }
            catch (StateCorruptException ex) { return new(JitOutcome.Blocked, ex.Message); }
            catch (OperationCanceledException) { return await HandleCancellationAsync(); }
            catch (Exception ex) when (ex is not OperationCanceledException) { return LostConnection(ex); }
        }

        public async Task<JitDeployResult> RecoverAsync(CancellationToken ct)
        {
            try
            {
                _state = await _store.ReadAsync(_t, ct);
                switch (ReleaseStateMachine.Recovery(_state.Phase))
                {
                    case RecoveryAction.None:
                        return new(JitOutcome.NothingToDeploy, "Nothing to recover: no deploy was interrupted.");
                    case RecoveryAction.Reset:
                        await ResetAsync();
                        return new(JitOutcome.NothingToDeploy, "A deploy that had not changed anything was interrupted; the saved state was cleaned up.");
                    case RecoveryAction.Commit:
                        await CommitAsync(CancellationToken.None);
                        return new(JitOutcome.Deployed, $"Release {_state.CurrentTag} had been verified; it is now recorded as the current release.", _state.CurrentTag);
                    default:
                        await Say("Recovering: undoing the interrupted deploy.");
                        return await RollbackAsync(null, "the deploy was interrupted");
                }
            }
            catch (StateCorruptException ex) { return new(JitOutcome.Blocked, ex.Message); }
            catch (Exception ex) when (ex is not OperationCanceledException) { return LostConnection(ex); }
        }

        public async Task<JitDeployResult> RollbackToAsync(string target, CancellationToken ct)
        {
            try
            {
                _state = await _store.ReadAsync(_t, ct);
                switch (ReleaseStateMachine.Recovery(_state.Phase))
                {
                    case RecoveryAction.None: break;
                    case RecoveryAction.Reset: await ResetAsync(); break;
                    case RecoveryAction.Commit: await CommitAsync(CancellationToken.None); break;
                    case RecoveryAction.Rollback:
                    {
                        await Say("A previous deploy was interrupted. Undoing it first.");
                        var r = await RollbackAsync(null, "the deploy was interrupted");
                        return r with { Message = "A previous deploy was interrupted, so it was undone first. " + r.Message + " Roll back again if you still want to." };
                    }
                    default:
                        return new(JitOutcome.Blocked, BlockedMessage());
                }

                if (_state.CurrentTag == target)
                    return new(JitOutcome.NothingToDeploy, $"Release {target} is already the one running.", target);
                var rec = _state.Releases.FirstOrDefault(r => r.Tag == target);
                if (rec is null)
                    return Failed($"Release {target} is not on this server (it may have been removed to save space), so it cannot be rolled back to.");

                await Say($"Checking that release {target} can still be started (docker, its images)...");
                var docker = await Exec(JitCommands.CheckDocker(), true, ct);
                if (docker.Exit != 0) return Failed(PreflightMessage(docker.Lines, "Docker is not available."));
                var imgs = await Exec(JitCommands.EnsureImagesExist(rec.Images), true, ct);
                if (imgs.Exit != 0) return Failed(PreflightMessage(imgs.Lines, $"The images of release {target} are no longer on this server."));
                var running = _state.Releases.FirstOrDefault(r => r.Tag == _state.CurrentTag);
                if (running is not null)
                {
                    var cur = await Exec(JitCommands.EnsureImagesExist(running.Images), true, ct);
                    if (cur.Exit != 0 && !_req.AllowWithoutRollback)
                        return Failed("The images of the release that is running now are missing, so if this went wrong it could not be undone. " + PreflightMessage(cur.Lines, ""));
                }

                await Move(DeployPhase.Syncing, ct: ct);
                await Move(DeployPhase.Building, s => s with { InProgressTag = target }, ct);

                async Task<JitDeployResult> Abort(string message)
                {
                    await Move(DeployPhase.FailedBuild, ct: CancellationToken.None);
                    await Move(DeployPhase.Idle, s => s with { InProgressTag = null }, CancellationToken.None);
                    return Failed(message);
                }

                await Say($"Restoring the compose file of release {target} (commit {rec.Sha[..7]})...");
                var checkout = await Exec(JitCommands.Checkout(_t, rec.Sha), true, ct);
                if (checkout.Exit != 0) return await Abort($"Could not check out commit {rec.Sha[..7]}: {Tail(checkout.Lines)}");
                if (await WriteOverrideAsync(ct) is { } overrideError) return await Abort(overrideError);
                var config = await Exec(JitCommands.ComposeConfig(_ctx, _bindings, target), false, ct);
                if (config.Exit != 0) return await Abort($"The compose file of that release is not valid: {Tail(config.Lines)}");

                IReadOnlyList<string> startOrder;
                try
                {
                    var graph = ComposeConfig.Graph(ComposeConfig.Parse(string.Join("\n", config.Lines)));
                    var managed = _req.Services.Select(s => s.Name).Where(graph.ContainsKey).ToList();
                    var affected = AffectedSet.Compute(graph, managed, _req.DependentChoices);
                    startOrder = DependencyOrderer.StartOrder(graph, affected.Select(a => a.Name));
                }
                catch (Exception ex) when (ex is ComposeConfigException or DependencyCycleException or UnknownDependencyException or ArgumentException)
                {
                    return await Abort(ex.Message);
                }

                await Move(DeployPhase.OldStopped, s => s with { InProgressTag = target, InProgressAffected = startOrder, InProgressReusesRelease = true }, ct);
                var swap = await SwapAsync(target, startOrder, ct);
                if (swap is not null) return swap;

                await Move(DeployPhase.Verified, ct: ct);
                await CommitAsync(CancellationToken.None);
                await Say($"Release {target} is live again.");
                await PruneAsync();
                return new(JitOutcome.Deployed, $"Rolled back on purpose: release {target} (commit {rec.Sha[..7]}) is live again.", target, rec.Sha);
            }
            catch (StateCorruptException ex) { return new(JitOutcome.Blocked, ex.Message); }
            catch (OperationCanceledException) { return await HandleCancellationAsync(); }
            catch (Exception ex) when (ex is not OperationCanceledException) { return LostConnection(ex); }
        }

        private static JitDeployResult LostConnection(Exception ex) =>
            new(JitOutcome.ConnectionLost,
                $"Lost contact with the server ({ex.Message}). The server's saved state records where the deploy got to; run recover (or deploy again, which recovers first) to finish or undo it safely.");

        private string BlockedMessage() =>
            $"A rollback failed earlier and the server needs attention before anything new is deployed. The previous release is {_state.CurrentTag ?? "(none)"}. " +
            "Fix the cause and run recover to retry the rollback, or restore it by hand.";

        private async Task ResetAsync()
        {
            await Say($"Cleaning up the leftover state of an earlier deploy that never changed anything ({_state.Phase}).");
            if (_state.Phase == DeployPhase.Syncing) await Move(DeployPhase.Idle, s => s with { InProgressTag = null });
            else if (_state.Phase == DeployPhase.Building)
            {
                await Move(DeployPhase.FailedBuild);
                await Move(DeployPhase.Idle, s => s with { InProgressTag = null });
            }
            else await Move(DeployPhase.Idle, s => s with { InProgressTag = null });
        }

        private async Task<JitDeployResult> HandleCancellationAsync()
        {
            var none = CancellationToken.None;
            try
            {
                switch (_state.Phase)
                {
                    case DeployPhase.Syncing:
                        await Move(DeployPhase.Idle, s => s with { InProgressTag = null }, none);
                        break;
                    case DeployPhase.Building:
                        await CleanupFailedBuildAsync(_state.InProgressTag);
                        break;
                    case DeployPhase.OldStopped or DeployPhase.NewStarted:
                        await Say("Cancelled after the old release was stopped: rolling back so the previous release keeps running.");
                        return await RollbackAsync(null, "the deploy was cancelled");
                }
                return Failed("The deploy was cancelled. Nothing was changed; the running release is untouched.");
            }
            catch (Exception ex) { return LostConnection(ex); }
        }

        // ── the deploy ───────────────────────────────────────────────────────

        /// <summary>
        /// Makes sure git, Docker and Docker Compose are on the server, installing only what is missing. Returns null
        /// when the server is ready, or the failure to report. Changes nothing but the installed tools, never the saved state.
        /// </summary>
        private async Task<JitDeployResult?> EnsureServerReadyAsync(CancellationToken ct)
        {
            await Say("[prepare] Checking what this server has (git, Docker, Docker Compose)…");
            var detect = await Exec(JitServerPrep.Detect(), false, ct);
            var missing = JitServerPrep.ParseMissing(detect.Lines);
            if (missing.Count == 0)
            {
                await Say("[prepare] Git, Docker and Docker Compose are already set up. Nothing to install.");
                return null;
            }

            var plan = JitServerPrep.Plan(missing);
            await Say("[prepare] This server is missing: " + string.Join(", ", missing.OrderBy(m => m).Select(JitServerPrep.Describe)) +
                      ". Installing it now. This is safe to repeat and only installs what is missing.");
            for (var i = 0; i < plan.Count; i++)
            {
                var step = plan[i];
                var label = $"Step {i + 1} of {plan.Count}";
                await Say($"[prepare] {label}: {step.Title}…");
                var (exit, lines) = await Exec(step.Command, true, ct, mergeStderr: true);
                if (exit != 0)
                {
                    await Say($"[prepare] {label} FAILED: {step.Title}.");
                    return Failed($"Could not prepare the server: '{step.Title}' failed ({Tail(lines)}). " +
                                  "Installing needs internet access and passwordless sudo on the server. Fix that, or install git, Docker and Docker Compose by hand, then deploy again.");
                }
                await Say($"[prepare] {label} done.");
            }

            var recheck = JitServerPrep.ParseMissing((await Exec(JitServerPrep.Detect(), false, ct)).Lines);
            if (recheck.Count > 0)
                return Failed("Could not prepare the server: after installing, these are still missing: " +
                              string.Join(", ", recheck.OrderBy(m => m).Select(JitServerPrep.Describe)) +
                              ". Install them by hand, then deploy again.");

            await Say("[prepare] The server is ready. Carrying on with the deploy.");
            return null;
        }

        /// <summary>Writes the generated build instructions beside the deploy state. Returns a message when that fails, or null.</summary>
        private async Task<string?> WriteOverrideAsync(CancellationToken ct)
        {
            var yaml = ComposeOverride.Render(_req.ComposeFile ?? string.Empty, _req.Services);
            if (yaml is null) return null;
            await Say("Writing the build instructions that compose will use…");
            var write = await Exec(ComposeOverride.WriteCommand(_t, yaml), false, ct);
            return write.Exit == 0 ? null : $"Could not write the build instructions on the server: {Tail(write.Lines)}";
        }

        private async Task<JitDeployResult> DeployCoreAsync(CancellationToken ct)
        {
            if (await EnsureServerReadyAsync(ct) is { } notReady) return notReady;

            // 1. Preflight: nothing is changed, not even the saved state.
            await Say("Checking the server (docker, free disk space, access to git)…");
            foreach (var (name, cmd) in new[]
            {
                ("docker", JitCommands.CheckDocker()),
                ("disk space", JitCommands.CheckDisk(_t, _s.MinFreeDiskMb)),
                ("git access", WithKey(JitCommands.CheckGit(_req.RepoUrl))),
            })
            {
                var (exit, lines) = await Exec(cmd, true, ct);
                if (exit != 0) return Failed(PreflightMessage(lines, $"The server check for {name} failed."));
            }

            if (_state.CurrentTag is { } currentTag)
            {
                var rec = _state.Releases.FirstOrDefault(r => r.Tag == currentTag);
                var (exit, lines) = rec is null ? (1, new List<string>()) : await Exec(JitCommands.EnsureImagesExist(rec.Images), true, ct);
                if (exit != 0)
                {
                    var why = PreflightMessage(lines, $"The images of the current release {currentTag} are not on this server, so a rollback would not be possible.");
                    if (!_req.AllowWithoutRollback) return Failed(why + " Deploy anyway only if you accept that there will be no rollback.");
                    await Say("Warning: " + why + " Continuing as requested.");
                }
            }
            else await Say("No previous release was built on this server, so a rollback will not be available for this deploy.");

            // 2. Sync to one exact commit.
            await Move(DeployPhase.Syncing, ct: ct);
            async Task<JitDeployResult> AbortSync(string message)
            {
                await Move(DeployPhase.Idle, s => s with { InProgressTag = null });
                return Failed(message);
            }

            await Say("Fetching the code from git…");
            var sync = await Exec(WithKey(JitCommands.SyncRepo(_t, _req.RepoUrl)), true, ct);
            if (sync.Exit != 0) return await AbortSync($"Could not fetch the code from git: {Tail(sync.Lines)}");

            var what = _req.Commit is null ? $"branch '{_req.Branch}'" : $"commit {_req.Commit}";
            var resolve = await Exec(_req.Commit is null ? JitCommands.ResolveBranch(_t, _req.Branch) : JitCommands.ResolveCommit(_t, _req.Commit), false, ct);
            var shaLine = resolve.Lines.Select(l => l.Trim()).LastOrDefault(l => l.Length > 0);
            if (resolve.Exit != 0 || shaLine is null) return await AbortSync($"The {what} was not found in the repository.");
            string sha;
            try { sha = JitNames.Sha(shaLine); }
            catch (ArgumentException) { return await AbortSync($"The {what} did not resolve to a commit."); }

            if (!_req.Force && _state.CurrentTag is { } cur && _state.Releases.FirstOrDefault(r => r.Tag == cur)?.Sha == sha)
            {
                await Move(DeployPhase.Idle, s => s with { InProgressTag = null });
                return new(JitOutcome.NothingToDeploy, $"Release {cur} already is commit {sha[..7]}; there is nothing new to deploy.", cur, sha);
            }

            var images = await Exec(JitCommands.ListImages(), false, ct);
            var onDisk = ImageListParser.ReleasesFrom(ImageListParser.Parse(images.Lines), _req.Services.Select(s => s.ImageRef).Distinct().ToList());
            var counter = ReleaseTag.NextCounter(_state.Releases.Select(r => r.Tag).Concat(onDisk.Select(r => r.Tag)));
            var tag = ReleaseTag.Format(counter, sha);
            await Say($"Commit {sha[..7]} will be release {tag}.");

            var checkout = await Exec(JitCommands.Checkout(_t, sha), true, ct);
            if (checkout.Exit != 0) return await AbortSync($"Could not check out commit {sha[..7]}: {Tail(checkout.Lines)}");

            if (await WriteOverrideAsync(ct) is { } overrideError) return await AbortSync(overrideError);

            // 3. Understand the compose file before building anything.
            var config = await Exec(JitCommands.ComposeConfig(_ctx, _bindings, tag), false, ct);
            if (config.Exit != 0) return await AbortSync($"The compose file is not valid: {Tail(config.Lines)}");

            IReadOnlyList<ComposeService> services;
            IReadOnlyList<AffectedService> affected;
            IReadOnlyList<string> startOrder;
            try
            {
                services = ComposeConfig.Parse(string.Join("\n", config.Lines));
                var graph = ComposeConfig.Graph(services);
                DependencyOrderer.StartOrder(graph);
                var problems = ComposeConfig.CheckBuilds(services, _req.Services.Select(s => new BuildExpectation(s.Name, s.TagVariable, ExpectedRef(s, tag))));
                if (problems.Count > 0)
                    return await AbortSync(string.Join(" ", problems) + (problems.Any(p => p.Contains("has no build: section"))
                        ? " Pick this service's Dockerfile with Browse for Dockerfile in the Build on the server card and ShipRight adds the build instructions for you, or add a build: section to your compose file."
                        : ""));

                affected = AffectedSet.Compute(graph, _req.Services.Select(s => s.Name), _req.DependentChoices);
                startOrder = DependencyOrderer.StartOrder(graph, affected.Select(a => a.Name));
            }
            catch (Exception ex) when (ex is ComposeConfigException or DependencyCycleException or UnknownDependencyException or ArgumentException)
            {
                return await AbortSync(ex.Message);
            }

            await Say("Services that will be restarted:");
            foreach (var a in affected) await Say($"  {a.Name}: {a.Reason}");

            // 4. Build everything while the old release keeps serving.
            await Move(DeployPhase.Building, s => s with { InProgressTag = tag }, ct);
            var buildNames = _req.Services.Select(s => s.Name).ToList();
            await Say($"Building {string.Join(", ", buildNames)} (the running release keeps serving until every build has finished)…");
            var build = await Exec(JitCommands.Build(_ctx, buildNames, _bindings, tag, _s.BuildNice), true, ct, mergeStderr: true);
            if (build.Exit != 0) return await FailBuildAsync(tag, $"The build failed, so nothing was changed: {Tail(build.Lines)}");

            var check = await Exec(JitCommands.EnsureImagesExist(ExpectedRefs(tag)), false, ct);
            if (check.Exit != 0) return await FailBuildAsync(tag, "The build finished but its images are missing, so nothing was changed.");
            await Say($"All images for {tag} are built.");

            // 5. The point of no return. The state is saved first, so a crash from here on can be undone.
            var record = new ReleaseRecord(tag, sha, ExpectedRefs(tag), _clock.UtcNow);
            await Move(DeployPhase.OldStopped, s => s with { InProgressTag = tag, InProgressAffected = startOrder, Releases = [.. s.Releases, record] }, ct);

            var swap = await SwapAsync(tag, startOrder, ct);
            if (swap is not null) return swap;

            // 6. Success.
            await Move(DeployPhase.Verified, ct: ct);
            await CommitAsync(CancellationToken.None);
            await Say($"Release {tag} is live (commit {sha[..7]}).");
            await PruneAsync();
            return new(JitOutcome.Deployed, $"Release {tag} (commit {sha[..7]}) is live.", tag, sha);
        }

        /// <summary>Stops the affected services (dependents first) and starts them from the given tag (dependencies first), checking each. Null on success, else the rolled-back result.</summary>
        private async Task<JitDeployResult?> SwapAsync(string tag, IReadOnlyList<string> startOrder, CancellationToken ct)
        {
            foreach (var svc in startOrder.Reverse())
            {
                await Say($"Stopping {svc}…");
                var stop = await Exec(JitCommands.StopService(_ctx, svc, _s.StopGraceSeconds, _bindings, tag), true, ct);
                if (stop.Exit != 0) return await RollbackAsync(svc, $"it could not be stopped ({Tail(stop.Lines, 1)})");
            }

            await Move(DeployPhase.NewStarted, ct: ct);
            foreach (var svc in startOrder)
            {
                await Say($"Starting {svc}…");
                var start = await Exec(JitCommands.StartService(_ctx, svc, _bindings, tag), true, ct);
                if (start.Exit != 0) return await RollbackAsync(svc, $"it failed to start ({Tail(start.Lines, 1)})");

                var gate = await GateAsync(svc, tag, ct);
                if (gate.Verdict != GateVerdict.Pass) return await RollbackAsync(svc, gate.Reason);
                await Say($"{svc} passed its health check: {gate.Reason}.");
            }

            return null;
        }

        // ── building ─────────────────────────────────────────────────────────

        private async Task<JitDeployResult> FailBuildAsync(string tag, string message)
        {
            await CleanupFailedBuildAsync(tag);
            return Failed(message);
        }

        private async Task CleanupFailedBuildAsync(string? tag)
        {
            var none = CancellationToken.None;
            if (tag is not null)
            {
                await Say($"Removing the partly built images of {tag}.");
                await Exec(JitCommands.RemoveImages(ExpectedRefs(tag)), true, none);
            }
            if (_state.Phase == DeployPhase.Building) await Move(DeployPhase.FailedBuild, ct: none);
            await Move(DeployPhase.Idle, s => s with { InProgressTag = null }, none);
        }

        // ── health gate ──────────────────────────────────────────────────────

        private async Task<GateDecision> GateAsync(string service, string tag, CancellationToken ct)
        {
            var options = new GateOptions(TimeSpan.FromSeconds(_s.HealthTimeoutSeconds), TimeSpan.FromSeconds(_s.SettleSeconds));
            var poll = TimeSpan.FromSeconds(_s.PollSeconds);
            var cap = (options.HealthTimeout > options.SettleTime ? options.HealthTimeout : options.SettleTime) + poll * 4;
            var began = _clock.UtcNow;
            var baseline = new Dictionary<int, int>();

            while (true)
            {
                var probe = await Exec(JitCommands.ProbeService(_ctx, service, _bindings, tag), false, ct);
                if (probe.Exit != 0) return new(GateVerdict.Fail, "its status could not be checked");

                IReadOnlyList<ProbeResult> results;
                try { results = ProbeParser.Parse(string.Join("\n", probe.Lines)); }
                catch (FormatException ex) { return new(GateVerdict.Fail, ex.Message); }
                if (results.Count == 0) return new(GateVerdict.Fail, "no container was created for it");

                var elapsed = _clock.UtcNow - began;
                var decisions = results.Select((p, i) =>
                {
                    baseline.TryAdd(i, p.RestartCount);
                    return HealthGate.Decide(new GateObservation(p.State, p.Health, p.RestartCount, baseline[i], elapsed), options);
                }).ToList();

                var bad = decisions.FirstOrDefault(d => d.Verdict == GateVerdict.Fail);
                if (bad is not null) return bad;
                if (decisions.All(d => d.Verdict == GateVerdict.Pass)) return decisions[0];
                if (elapsed > cap) return new(GateVerdict.Fail, $"it did not settle within {(int)cap.TotalSeconds} seconds");

                await _clock.DelayAsync(poll, ct);
            }
        }

        // ── commit and retention ─────────────────────────────────────────────

        private Task CommitAsync(CancellationToken ct) =>
            Move(DeployPhase.Idle, s => s with
            {
                PreviousTag = s.CurrentTag,
                CurrentTag = s.InProgressTag,
                InProgressTag = null,
                InProgressAffected = [],
                InProgressReusesRelease = false,
            }, ct);

        /// <summary>Removes releases beyond the keep count. It can never fail the deploy.</summary>
        private async Task PruneAsync()
        {
            try
            {
                var none = CancellationToken.None;
                var repos = _req.Services.Select(s => s.ImageRef).Distinct().ToList();
                var all = await Exec(JitCommands.ListImages(), false, none);
                var running = await Exec(JitCommands.ListContainerImages(), false, none);
                if (all.Exit != 0 || running.Exit != 0) { await Say("Skipping cleanup of old releases: the image list could not be read."); return; }

                var onDisk = ImageListParser.ReleasesFrom(ImageListParser.Parse(all.Lines), repos);
                var known = _state.Releases.Select(r => new ReleaseInfo(r.Tag, r.Images)).ToList();
                foreach (var d in onDisk)
                {
                    var i = known.FindIndex(k => k.Tag == d.Tag);
                    if (i < 0) known.Add(d); else known[i] = new ReleaseInfo(d.Tag, known[i].Images.Union(d.Images).ToList());
                }

                var prune = ReleaseRetention.SelectToPrune(known, _s.ReleasesToKeep, _state.CurrentTag, _state.PreviousTag,
                    ImageListParser.Parse(running.Lines).ToHashSet(StringComparer.Ordinal));
                if (prune.Count == 0) return;

                await Say($"Removing old releases beyond the {Math.Max(_s.ReleasesToKeep, ReleaseRetention.MinimumToKeep)} kept: {string.Join(", ", prune)}.");
                await Exec(JitCommands.RemoveImages(prune.SelectMany(t => known.First(k => k.Tag == t).Images)), true, none);
                await Save(_state with { Releases = _state.Releases.Where(r => !prune.Contains(r.Tag)).ToList() }, none);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await Say($"Warning: cleaning up old releases failed ({ex.Message}). The deploy itself succeeded.");
            }
        }

        // ── rollback ─────────────────────────────────────────────────────────

        private async Task<JitDeployResult> RollbackAsync(string? failedService, string reason)
        {
            var none = CancellationToken.None;
            var failedTag = _state.InProgressTag;
            var startOrder = _state.InProgressAffected;
            var prevTag = _state.CurrentTag;
            var because = failedService is null ? $"The deploy stopped because {reason}." : $"{failedService} {reason}.";

            await Say($"{(failedService is null ? "Stopping" : $"{failedService} failed ({reason}). Stopping")} the new release and rolling back…");
            await Move(DeployPhase.RollingBack, ct: none);

            if (failedTag is null)
            {
                await Move(DeployPhase.RolledBack, ct: none);
                await Move(DeployPhase.Idle, ct: none);
                return new(JitOutcome.RolledBack, "There was no release in progress to roll back.");
            }

            async Task StopAll()
            {
                foreach (var svc in startOrder.Reverse())
                {
                    await Say($"Stopping {svc}…");
                    var r = await Exec(JitCommands.StopService(_ctx, svc, _s.StopGraceSeconds, _bindings, failedTag), true, none);
                    if (r.Exit != 0) await Say($"Warning: {svc} did not stop cleanly; continuing.");
                }
            }

            await StopAll();

            var reused = _state.InProgressReusesRelease;
            async Task ForgetFailedRelease()
            {
                if (reused) await Say($"Release {failedTag} is kept: it was an older release you chose to go back to.");
                else
                {
                    await Say($"Removing the images of the failed release {failedTag}.");
                    await Exec(JitCommands.RemoveImages(ExpectedRefs(failedTag)), true, none);
                }
                await Move(DeployPhase.RolledBack, s => s with
                {
                    InProgressTag = null,
                    InProgressAffected = [],
                    InProgressReusesRelease = false,
                    Releases = reused ? s.Releases : s.Releases.Where(r => r.Tag != failedTag).ToList(),
                }, none);
                await Move(DeployPhase.Idle, ct: none);
            }

            if (prevTag is null)
            {
                await ForgetFailedRelease();
                return new(JitOutcome.FailedNoPreviousRelease,
                    $"{because} There is no previous release on this server to restore, so the services that were stopped stay stopped. Fix the problem and deploy again.",
                    failedTag, null, failedService);
            }

            var prev = _state.Releases.FirstOrDefault(r => r.Tag == prevTag);
            if (prev is not null)
                for (var attempt = 1; attempt <= 2; attempt++)
                {
                    await Say($"Restoring release {prevTag} (attempt {attempt} of 2)…");
                    if (await RestoreAsync(prev, startOrder)) goto restored;
                    await Say($"Restoring {prevTag} did not work on attempt {attempt}.");
                    if (attempt == 1) await StopAll();
                }

            await Move(DeployPhase.RollbackFailed, ct: none);
            return new(JitOutcome.RollbackFailed,
                $"{because} Rolling back to {prevTag} did not work either, and the server may be partly stopped. " +
                "To restore it by hand, run these on the server, in order:\n" + ManualCommands(prev, prevTag, startOrder),
                failedTag, null, failedService);

            restored:
            await ForgetFailedRelease();
            await Say($"Release {prevTag} is running again.");
            return new(JitOutcome.RolledBack,
                reused
                    ? $"{because} Rolled back: release {prevTag} is running again, and release {failedTag} is still kept."
                    : $"{because} Rolled back: release {prevTag} is running again and the failed release {failedTag} was removed.",
                prevTag, prev.Sha, failedService);
        }

        private async Task<bool> RestoreAsync(ReleaseRecord prev, IReadOnlyList<string> startOrder)
        {
            var none = CancellationToken.None;
            var checkout = await Exec(JitCommands.Checkout(_t, prev.Sha), true, none);
            if (checkout.Exit != 0) return false;

            var order = startOrder;
            var config = await Exec(JitCommands.ComposeConfig(_ctx, _bindings, prev.Tag), false, none);
            if (config.Exit == 0)
            {
                try
                {
                    var existing = ComposeConfig.Parse(string.Join("\n", config.Lines)).Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
                    order = startOrder.Where(existing.Contains).ToList();   // a service added by the failed release has nothing to restore
                }
                catch (ComposeConfigException) { }
            }

            foreach (var svc in order)
            {
                await Say($"Starting {svc} from release {prev.Tag}…");
                var start = await Exec(JitCommands.StartService(_ctx, svc, _bindings, prev.Tag), true, none);
                if (start.Exit != 0) return false;
                var gate = await GateAsync(svc, prev.Tag, none);
                if (gate.Verdict != GateVerdict.Pass) { await Say($"{svc} is not healthy after the restore: {gate.Reason}."); return false; }
            }
            return true;
        }

        private string ManualCommands(ReleaseRecord? prev, string prevTag, IReadOnlyList<string> startOrder)
        {
            var steps = new List<string>();
            if (prev is not null) steps.Add(JitCommands.Checkout(_t, prev.Sha));
            steps.AddRange(startOrder.Select(s => JitCommands.StartService(_ctx, s, _bindings, prevTag)));
            return string.Join("\n", steps.Select((s, i) => $"  {i + 1}. {s}"));
        }
    }
}
