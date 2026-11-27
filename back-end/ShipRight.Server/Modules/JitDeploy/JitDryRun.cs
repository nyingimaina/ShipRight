using ShipRight.Modules.Projects;
using ShipRight.Shared.ProcessRunner;
using ShipRight.Shared.SshRunner;

namespace ShipRight.Modules.JitDeploy;

/// <summary>What a dry run found. Nothing in this result means anything on the target changed.</summary>
public sealed record JitDryRunResult
{
    public bool Ok { get; init; }
    public bool NotFound { get; init; }
    public string? Error { get; init; }
    public string? AttemptId { get; init; }
    public string? DiagnosticsUrl { get; init; }
    public string Branch { get; init; } = "master";
    public string? Commit { get; init; }
    public string? Tag { get; init; }
    public IReadOnlyList<string> Services { get; init; } = [];
    public IReadOnlyList<JitPlanCommand> Commands { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public IReadOnlyList<string> Problems { get; init; } = [];
    public string? CurrentTag { get; init; }
    public string? Phase { get; init; }
    public string? GuardSummary { get; init; }
    public bool TargetChecked { get; init; }
}

/// <summary>
/// Answers "what would a deploy do?" without doing any of it. It runs the same mapping, credential resolution and local
/// guard as a real deploy, resolves the branch tip, and reads the target with read-only commands only. It never
/// enters <see cref="JitDeployer"/>: no clone, fetch, reset, clean, checkout, env/state write, build, stop, start or
/// prune is ever sent, so a dry run against production is safe.
/// </summary>
public sealed class JitDryRunService(
    IProjectStore projects, IJitConfigStore configs, JitLocalBranchGuard localBranchGuard,
    ISshRunner ssh, IProcessRunner runner, JitDeployAttemptStore attempts,
    ShipRight.Modules.Resources.Stores.ICredentialResourceStore? credentials = null,
    Modules.Servers.IServerStore? servers = null)
{
    public async Task<JitDryRunResult> PlanAsync(string projectId, JitDeployOptions? options, string? initiator, CancellationToken ct = default)
    {
        options ??= new JitDeployOptions();

        var project = await projects.GetByIdAsync(projectId);
        if (project is null)
            return new() { Ok = false, NotFound = true, Error = $"Project '{projectId}' not found." };

        JitDeployConfig? cfg;
        try { cfg = await configs.GetAsync(projectId); }
        catch (InvalidDataException ex) { return new() { Ok = false, Error = ex.Message }; }

        var attempt = new JitAttemptBuilder(attempts, project, cfg, initiator, dryRun: true, DateTime.UtcNow);
        var warnings = new List<string>();

        var serverEnv = project.Server.EnvFile;
        if (servers is not null)
        {
            var serverId = string.IsNullOrWhiteSpace(project.Server.Id) ? project.ServerId : project.Server.Id;
            if (!string.IsNullOrWhiteSpace(serverId) && await servers.GetByIdAsync(serverId) is { } serverRecord)
                serverEnv = serverRecord.EnvFile;
        }

        var map = JitEnvMapper.MapWithServerEnv(project, cfg, serverEnv, options.Commit, options.Force, options.AllowWithoutRollback);
        if (!map.Ok)
        {
            var message = JitServiceHelp.StartMessage(map.Problems);
            attempt.AddProblems(map.Problems);
            return await FinishAsync(attempt, warnings, false, message, cfg, options, commit: null, tag: null,
                services: [], commands: [], guardSummary: null, targetChecked: false, currentTag: null, phase: null, ct: ct);
        }

        JitGuardReport? guard = null;
        if (string.IsNullOrWhiteSpace(options.Commit) && cfg is not null)
        {
            var local = await localBranchGuard.CheckAsync(project, cfg, ct);
            guard = local.Report;
            attempt.WithGuard(guard);
            warnings.AddRange(local.Warnings);
            if (local.Problem is not null)
                return await FinishAsync(attempt, warnings, false, local.Problem, cfg, options, commit: null, tag: null,
                    services: [], commands: [], guardSummary: guard?.Problem is null ? null : "Blocked: " + guard.Problem,
                    targetChecked: false, currentTag: null, phase: null, ct: ct);
        }
        else
        {
            attempt.WithNotes("An explicit commit was given, so the local checkout guard was not run.");
        }

        var (withCredential, credentialError) = await JitCredentialResolver.ApplyAsync(map.Request!, cfg, credentials, projectId);
        if (credentialError is not null)
            return await FinishAsync(attempt, warnings, false, credentialError, cfg, options, commit: null, tag: null,
                services: [], commands: [], guardSummary: GuardSummary(guard), targetChecked: false, currentTag: null, phase: null, ct: ct);

        if (withCredential?.GitCredential is { } secret) attempt.Secret(secret.Token);
        var request = withCredential! with { RebuildAll = options.RebuildAll };
        attempt.WithCommit(request.Commit);
        var services = request.Services.Select(s => s.Name).ToList();

        if (request.Settings.StopGraceSeconds < 0 || request.Settings.HealthTimeoutSeconds <= 0 ||
            request.Settings.SettleSeconds < 0 || request.Settings.MinFreeDiskMb <= 0 ||
            request.Settings.PollSeconds <= 0 || request.Settings.ReleasesToKeep < 0)
            return await FinishAsync(attempt, warnings, false, "A timeout or count in the settings is out of range.", cfg, options,
                commit: request.Commit, tag: null, services: services, commands: [], guardSummary: GuardSummary(guard),
                targetChecked: false, currentTag: null, phase: null, ct: ct);

        string? commit;
        try
        {
            commit = string.IsNullOrWhiteSpace(options.Commit) ? null : JitNames.Sha(options.Commit.Trim());
        }
        catch (ArgumentException ex)
        {
            return await FinishAsync(attempt, warnings, false, ex.Message, cfg, options,
                commit: null, tag: null, services: services, commands: [], guardSummary: GuardSummary(guard),
                targetChecked: false, currentTag: null, phase: null, ct: ct);
        }

        if (commit is null)
        {
            commit = guard?.Repos.Where(r => r.RemoteRefreshed).Select(r => r.RemoteRef).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))
                     ?? guard?.Repos.Select(r => r.RemoteRef).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
            if (commit is null)
            {
                commit = await TryResolveTipAsync(project, cfg, ct);
                if (commit is null)
                    warnings.Add("Could not read the branch tip locally. A real deploy resolves it on the server just before building.");
            }
        }
        attempt.WithCommit(commit);

        // ── read-only look at the target ─────────────────────────────────────
        var targetChecked = false;
        var currentTag = (string?)null;
        var phase = (string?)null;
        ReleaseState state = ReleaseState.Empty;
        try
        {
            var docker = await ExecAsync(request, JitCommands.CheckDocker(), ct);
            targetChecked = true;
            if (docker.Exit != 0)
                warnings.Add(PreflightMessage(docker.Lines, "Docker is not available on the server."));

            var disk = await ExecAsync(request, JitCommands.FreeDisk(request.Target), ct);
            if (disk.Exit != 0)
                warnings.Add("Could not read the server's free disk space.");
            else if (JitCommands.TryParseFreeMb(disk.Lines) is { } free && free < request.Settings.MinFreeDiskMb)
                warnings.Add($"The server has {free} MB free; a real deploy refuses to start below {request.Settings.MinFreeDiskMb} MB.");

            var git = await ExecAsync(request, ExecutableKey(request, JitCommands.CheckGit(request.RepoUrl)), ct);
            if (git.Exit != 0)
                warnings.Add(PreflightMessage(git.Lines, "The server cannot reach the git repository."));

            try
            {
                state = await new ReleaseStateStore(ssh).ReadAsync(request.Target, ct);
                currentTag = state.CurrentTag;
                phase = state.Phase.ToString();
                if (ReleaseStateMachine.Recovery(state.Phase) != RecoveryAction.None)
                    warnings.Add($"The server's saved deploy state is {state.Phase}; a real deploy would recover or clean that up before building anything.");
            }
            catch (StateCorruptException ex) { warnings.Add(ex.Message); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                warnings.Add($"Could not read the server's saved deploy state: {ex.Message}");
            }

            if (state.CurrentTag is { } running)
            {
                var images = state.Releases.FirstOrDefault(r => r.Tag == running);
                if (images is null)
                    warnings.Add($"The current release {running} is not in the server's saved state, so a rollback may not be possible.");
                else
                {
                    var check = await ExecAsync(request, JitCommands.EnsureImagesExist(images.Images), ct);
                    if (check.Exit != 0)
                        warnings.Add(PreflightMessage(check.Lines, $"The images of the current release {running} are missing from the server, so a rollback would not be possible."));
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { warnings.Add($"Could not read the server: {ex.Message}. Nothing was changed."); }

        // ── the plan ─────────────────────────────────────────────────────────
        var commands = new List<JitPlanCommand>();
        string? tag = null;
        try
        {
            var ctx = new ComposeContext(request.Target, request.Project, request.EnvFile, request.ComposeFile,
                request.Services.Any(s => s.Recipe is not null) ? ComposeOverride.Path(request.Target) : null);
            var bindings = request.Services.Select(s => new TagBinding(s.TagVariable, s.ImageRef)).ToList();
            var placeholder = commit ?? "0000000";
            var imageList = await ExecAsync(request, JitCommands.ListImages(), ct);
            var onDisk = ImageListParser.ReleasesFrom(ImageListParser.Parse(imageList.Lines),
                request.Services.Select(s => s.ImageRef).Distinct(StringComparer.Ordinal).ToList());
            tag = ReleaseTag.Format(ReleaseTag.NextCounter(state.Releases.Select(r => r.Tag).Concat(onDisk.Select(r => r.Tag))), placeholder);
            if (commit is null) warnings.Add($"The release tag {tag} is provisional: it is renumbered when the server resolves the real commit.");

            commands.Add(new("Check the server", JitCommands.CheckDocker()));
            commands.Add(new("Check the server", JitCommands.FreeDisk(request.Target)));
            commands.Add(new("Check the server", PlanKey(request, JitCommands.CheckGit(request.RepoUrl))));
            commands.Add(new("Fetch the code", PlanKey(request, JitCommands.SyncRepo(request.Target, request.RepoUrl))));
            commands.Add(new("Resolve the commit", commit is null
                ? JitCommands.ResolveBranch(request.Target, request.Branch)
                : JitCommands.ResolveCommit(request.Target, commit)));
            commands.Add(new("Check out the commit", JitCommands.CheckoutKeepingData(request.Target, placeholder)));
            if (ctx.OverrideFile is not null)
                commands.Add(new("Add the build instructions", $"write {ctx.OverrideFile} (generated from the chosen Dockerfiles; not shown here)"));
            if (request.EnvContent is not null)
                commands.Add(new("Write the variables", $"write {request.EnvFile} ({request.EnvNote ?? "merged variables"}; values never shown)"));
            commands.Add(new("Read the compose file", JitCommands.ComposeConfig(ctx, bindings, tag)));
            commands.Add(new("Build the images", JitCommands.Build(ctx, services, bindings, tag, request.Settings.BuildNice)));

            foreach (var s in Enumerable.Reverse(services))
                commands.Add(new("Stop the old services (dependents first)", JitCommands.StopService(ctx, s, request.Settings.StopGraceSeconds, bindings, tag)));
            foreach (var s in services)
            {
                commands.Add(new("Start the new services (dependencies first)", JitCommands.StartService(ctx, s, bindings, tag)));
                commands.Add(new("Health check", JitCommands.ProbeService(ctx, s, bindings, tag)));
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return await FinishAsync(attempt, warnings, false, $"The plan could not be built: {ex.Message}", cfg, options,
                commit: commit, tag: tag, services: services, commands: commands, guardSummary: GuardSummary(guard),
                targetChecked: targetChecked, currentTag: currentTag, phase: phase, ct: ct);
        }

        return await FinishAsync(attempt, warnings, true, null, cfg, options,
            commit: commit, tag: tag, services: services, commands: commands, guardSummary: GuardSummary(guard),
            targetChecked: targetChecked, currentTag: currentTag, phase: phase, ct: ct);
    }

    private async Task<JitDryRunResult> FinishAsync(
        JitAttemptBuilder attempt, IReadOnlyList<string> warnings, bool ok, string? error,
        JitDeployConfig? cfg, JitDeployOptions options,
        string? commit, string? tag, IReadOnlyList<string> services, IReadOnlyList<JitPlanCommand> commands,
        string? guardSummary, bool targetChecked, string? currentTag, string? phase, CancellationToken ct)
    {
        attempt.Warn(warnings).Plan(commands).WithCommit(commit).WithServices(services).WithTag(tag)
            .WithOutcome(ok ? "Planned" : "Blocked", error);
        if (!ok && error is not null && attempt.Problems.Count == 0) attempt.Problem(error);
        await attempt.SaveAsync(ct);

        string? Safe(string? text) => text is null ? null : GitAddressForms.Redact(text, attempt.Secrets);
        var safeWarnings = warnings.Select(w => Safe(w)!).ToList();
        var safeProblems = attempt.Problems.Select(p => Safe(p)!).ToList();

        return new JitDryRunResult
        {
            Ok = ok,
            Error = Safe(error),
            AttemptId = attempt.Id,
            DiagnosticsUrl = attempt.Url,
            Branch = cfg?.Branch ?? options.Commit ?? string.Empty,
            Commit = commit,
            Tag = tag,
            Services = services,
            Commands = commands,
            Warnings = safeWarnings,
            Problems = safeProblems,
            CurrentTag = currentTag,
            Phase = phase,
            GuardSummary = Safe(guardSummary),
            TargetChecked = targetChecked,
        };
    }

    private static string? GuardSummary(JitGuardReport? guard)
    {
        if (guard is null) return null;
        if (!guard.Applicable) return "No matching local checkout was found, so nothing local was checked.";
        if (guard.Problem is not null) return "Blocked: " + guard.Problem;
        return $"Allowed: {guard.Repos.Count} local checkout{(guard.Repos.Count == 1 ? "" : "s")} checked, '{guard.Branch}' is fully pushed.";
    }

    /// <summary>Reads the branch tip locally. It is read-only and never touches the target.</summary>
    private async Task<string?> TryResolveTipAsync(ProjectConfig project, JitDeployConfig? cfg, CancellationToken ct)
    {
        if (cfg is null || string.IsNullOrWhiteSpace(cfg.RepoUrl) || string.IsNullOrWhiteSpace(cfg.Branch)) return null;

        string branch;
        string url;
        try { branch = JitNames.Branch(cfg.Branch); url = JitNames.RepoUrl(cfg.RepoUrl); }
        catch (ArgumentException) { return null; }

        var wanted = GitAddressForms.Analyse(url).Key;
        var path = project.GitRepos
            .Where(g => !string.IsNullOrWhiteSpace(g.RepoPath))
            .Select(g => g.RepoPath.Trim())
            .FirstOrDefault(p => string.Equals(GitAddressForms.Analyse(p).Key, wanted, StringComparison.OrdinalIgnoreCase));

        var args = new List<string>();
        if (path is not null) args.AddRange(["-C", path, "ls-remote", "--heads", "origin", $"refs/heads/{branch}"]);
        else args.AddRange(["ls-remote", "--heads", url, $"refs/heads/{branch}"]);

        try
        {
            var result = await runner.RunAsync("git", [.. args], null, ct: ct, timeout: TimeSpan.FromSeconds(60));
            if (!result.Success) return null;
            return ParseTip(result.StdOut, branch);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    internal static string? ParseTip(string output, string branch)
    {
        foreach (var raw in output.Split('\n'))
        {
            var parts = raw.Trim().Split(['\t', ' '], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            if (parts[1] != $"refs/heads/{branch}") continue;
            var sha = parts[0].Trim();
            return sha.Length is >= 7 and <= 64 && sha.All(Uri.IsHexDigit) ? sha.ToLowerInvariant() : null;
        }
        return null;
    }

    private async Task<(int Exit, List<string> Lines)> ExecAsync(JitDeployRequest request, string command, CancellationToken ct)
    {
        var lines = new List<string>();
        var exit = await ssh.RunAsync(request.Target.Host, request.Target.Username, request.Target.SshKeyPath, command,
            l => { lock (lines) lines.Add(l); return Task.CompletedTask; },
            _ => Task.CompletedTask, ct);
        lock (lines) return (exit, lines.ToList());
    }

    /// <summary>The real command, including the saved credential. It is executed but never printed.</summary>
    private static string ExecutableKey(JitDeployRequest request, string command) =>
        request.GitCredential is { } secret ? JitGitCredential.Wrap(command, secret)
        : request.DeployKeyName is { } key ? JitDeployKey.GitSshPrefix(key) + command
        : command;

    /// <summary>The plan line: says which credential would be used without printing the token.</summary>
    private static string PlanKey(JitDeployRequest request, string command) =>
        request.GitCredential is not null ? "[using the saved credential] " + command
        : request.DeployKeyName is { } key ? JitDeployKey.GitSshPrefix(key) + command
        : command;

    private static string PreflightMessage(IEnumerable<string> lines, string fallback)
    {
        var p = lines.Where(l => l.StartsWith("[preflight]", StringComparison.Ordinal))
            .Select(l => l["[preflight]".Length..].Trim()).ToList();
        return p.Count > 0 ? string.Join(" ", p) : fallback;
    }
}
