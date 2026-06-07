using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Serilog;
using Serilog.Context;
using ShipRight.Modules.Projects;
using ShipRight.Modules.VersionFiles;
using ShipRight.Shared.Events;
using ShipRight.Shared.ProcessRunner;
using ShipRight.Shared.SshRunner;

namespace ShipRight.Modules.Builds;

public record StartBuildRequest(string ProjectId, List<ServiceVersionInput> ServiceVersions);
public record ServiceVersionInput(string ServiceName, string NewVersion);
public record RespondRequest(string Reason, string Choice, Dictionary<string, string>? Data);

public class BuildOrchestrator
{
    private readonly IBuildStore _buildStore;
    private readonly IProjectStore _projectStore;
    private readonly BuildEventBus _bus;
    private readonly IProcessRunner _runner;
    private readonly ISshRunner _ssh;
    private readonly Dictionary<string, TaskCompletionSource<RespondRequest>> _pauseWaiters = new();
    private static readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellations = new();
    private static readonly Dictionary<string, int> _stepNumbers = new()
    {
        ["PreconditionCheck"] = 1, ["GitStatusCheck"] = 2, ["BranchCheck"] = 3,
        ["WriteVersionsAndTag"] = 4, ["ComposeRepoSync"] = 5, ["DockerBuild"] = 6, ["BuildComplete"] = 7,
    };

    public bool CancelBuild(string buildId)
    {
        if (!_cancellations.TryGetValue(buildId, out var cts)) return false;
        cts.Cancel();
        return true;
    }

    public BuildOrchestrator(IBuildStore buildStore, IProjectStore projectStore,
        BuildEventBus bus, IProcessRunner runner, ISshRunner ssh)
    {
        _buildStore = buildStore;
        _projectStore = projectStore;
        _bus = bus;
        _runner = runner;
        _ssh = ssh;
    }

    public async Task<BuildRecord> StartAsync(StartBuildRequest request)
    {
        var project = await _projectStore.GetByIdAsync(request.ProjectId)
            ?? throw new InvalidOperationException($"Project '{request.ProjectId}' not found.");

        var versions = request.ServiceVersions.Select(sv => new ServiceVersion
        {
            ServiceName = sv.ServiceName,
            NewVersion = sv.NewVersion,
            PreviousVersion = project.Services.FirstOrDefault(s => s.Name == sv.ServiceName)
                is { } svc ? TryReadVersion(svc.VersionFilePath) : "",
            DockerImageName = project.Services.FirstOrDefault(s => s.Name == sv.ServiceName)?.DockerImageName ?? "",
        }).ToList();

        var record = new BuildRecord
        {
            ProjectId = project.Id,
            ProjectName = project.Name,
            Status = BuildStatus.Running,
            Versions = versions,
        };

        await _buildStore.SaveAsync(record);
        _bus.Register(record.Id);
        _ = Task.Run(() => RunPipelineAsync(record, project));
        return record;
    }

    public async Task DeployAsync(string buildId)
    {
        var record = await _buildStore.GetByIdAsync(buildId);
        if (record is null) return;

        var project = await _projectStore.GetByIdAsync(record.ProjectId);
        if (project is null) return;

        using var _ = LogContext.PushProperty("BuildId", buildId);
        var ctx = new PipelineContext(record, _bus);

        record.Status = BuildStatus.Deploying;
        await _buildStore.SaveAsync(record);
        var deployStartedAt = DateTime.UtcNow;

        try
        {
            await ctx.EmitLogAsync($"Connecting to {project.Server.Username}@{project.Server.Host}…", "ssh");

            var cmd = project.Server.DeployMode switch
            {
                DeployMode.SemiManaged  => BuildSemiManagedDeployCmd(project),
                DeployMode.FullyManaged => BuildFullyManagedDeployCmd(project, record.Versions),
                _                       => $"cd {project.Server.RemoteWorkingDir} && bash {project.Server.RebuildScript}"
            };
            await ctx.EmitLogAsync($"Deploy mode: {project.Server.DeployMode}", "shipright");
            var exitCode = await _ssh.RunAsync(
                project.Server.Host,
                project.Server.Username,
                project.Server.SshKeyPath,
                cmd,
                line => ctx.EmitLogAsync(line, "ssh"));

            if (exitCode != 0)
            {
                record.Status = BuildStatus.DeployFailed;
                record.ErrorSummary = $"rebuild.sh exited with code {exitCode}. Check server state manually.";
                Log.Error("Deployment {BuildId} failed: exit code {ExitCode}", buildId, exitCode);
            }
            else
            {
                record.Status = BuildStatus.Deployed;
                record.DeployedAt = DateTime.UtcNow;
                Log.Information("Deployment {BuildId} succeeded", buildId);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "SSH connection lost during deployment {BuildId}", buildId);
            record.Status = BuildStatus.DeployFailed;
            record.ErrorSummary = $"SSH connection lost: {ex.Message}. Remote state is unknown — check {project.Server.Host} manually.";
            await ctx.EmitLogAsync($"[ERROR] SSH connection lost: {ex.Message}", "ssh");
        }

        record.StepDurations["Deploy"] = (int)(DateTime.UtcNow - deployStartedAt).TotalSeconds;
        record.LogOutput += $"\n[Deployment finished: {record.Status}]";
        await _buildStore.SaveAsync(record);
        await ctx.DeployCompletedAsync();
    }

    public async Task<bool> RespondAsync(string buildId, RespondRequest response)
    {
        if (!_pauseWaiters.TryGetValue(buildId, out var tcs)) return false;
        tcs.SetResult(response);
        _pauseWaiters.Remove(buildId);
        return true;
    }

    private async Task RunPipelineAsync(BuildRecord record, ProjectConfig project)
    {
        using var logBuildId = LogContext.PushProperty("BuildId", record.Id);
        using var logProjectId = LogContext.PushProperty("ProjectId", record.ProjectId);

        var ctx = new PipelineContext(record, _bus);

        async Task SaveStep() => await _buildStore.SaveAsync(record);

        using var cts = new CancellationTokenSource();
        _cancellations[record.Id] = cts;
        var ct = cts.Token;

        try
        {
            Log.Information("Build {BuildId} pipeline started", record.Id);

            // ── Step 1: Precondition Check ────────────────────────────────────
            await ctx.StepStartedAsync(1, "PreconditionCheck");
            foreach (var svc in project.Services)
            {
                await ctx.EmitLogAsync($"Checking version file: {svc.VersionFilePath}");
                if (!File.Exists(svc.VersionFilePath))
                    throw new InvalidOperationException($"version.txt not found: {svc.VersionFilePath}");
                await ctx.EmitLogAsync($"Checking build context: {svc.BuildContextPath}");
                if (!Directory.Exists(svc.BuildContextPath))
                    throw new InvalidOperationException($"Build context not found: {svc.BuildContextPath}");
            }
            foreach (var repo in project.GitRepos)
            {
                await ctx.EmitLogAsync($"Checking git repo: {repo.RepoPath}");
                if (!Directory.Exists(repo.RepoPath))
                    throw new InvalidOperationException($"Git repo not found: {repo.RepoPath}");
            }

            await ctx.EmitLogAsync("Checking Docker daemon…");
            var dockerInfo = await _runner.RunAsync("docker", ["info"], null);
            if (!dockerInfo.Success)
                throw new InvalidOperationException("Docker daemon not running or not accessible.");

            await ctx.EmitLogAsync("Preconditions satisfied.", "shipright");
            await ctx.StepCompletedAsync(1, "PreconditionCheck");
            await SaveStep();

            // ── Step 2: Git Status Check ──────────────────────────────────────
            await ctx.StepStartedAsync(2, "GitStatusCheck");
            var dirtyRepos = new List<string>();
            foreach (var repo in project.GitRepos)
            {
                await ctx.EmitLogAsync($"Checking git status in {repo.RepoPath}…");
                var gitStatus = await _runner.RunAsync("git",
                    ["-C", repo.RepoPath, "status", "--porcelain"],
                    null,
                    line => ctx.EmitLogAsync(line, "git"));
                if (!gitStatus.Success)
                    throw new InvalidOperationException($"git status failed in {repo.RepoPath}:\n{gitStatus.StdErr}");
                if (!string.IsNullOrWhiteSpace(gitStatus.StdOut))
                    dirtyRepos.Add(repo.RepoPath);
            }

            if (dirtyRepos.Count > 0)
            {
                await ctx.PauseAsync("git_dirty",
                    $"Uncommitted changes in {dirtyRepos.Count} repo(s). Commit and push, or commit only?",
                    ["commit_and_push", "commit", "abort"]);
                await SaveStep();

                var tcs = new TaskCompletionSource<RespondRequest>();
                _pauseWaiters[record.Id] = tcs;
                var response = await tcs.Task;

                if (response.Choice == "abort")
                {
                    record.Status = BuildStatus.Aborted;
                    await SaveStep();
                    await ctx.EmitLogAsync("Build aborted by user.", "shipright");
                    await ctx.BuildCompletedAsync();
                    return;
                }

                var msg = response.Data?.GetValueOrDefault("commitMessage")
                    ?? "[ShipRight auto-commit] Pre-build snapshot";

                foreach (var repo in project.GitRepos.Where(r => dirtyRepos.Contains(r.RepoPath)))
                {
                    await ctx.EmitLogAsync($"Staging changes in {repo.RepoPath}…");
                    var addResult = await _runner.RunAsync("git",
                        ["-C", repo.RepoPath, "add", "-A"], null,
                        line => ctx.EmitLogAsync(line, "git"));
                    if (!addResult.Success)
                        throw new InvalidOperationException($"git add failed in {repo.RepoPath}:\n{addResult.StdErr}");

                    await ctx.EmitLogAsync($"Committing in {repo.RepoPath}…");
                    var commitResult = await _runner.RunAsync("git",
                        ["-C", repo.RepoPath, "commit", "-m", msg], null,
                        line => ctx.EmitLogAsync(line, "git"));
                    if (!commitResult.Success)
                        throw new InvalidOperationException($"git commit failed in {repo.RepoPath}:\n{commitResult.StdErr}");

                    if (response.Choice == "commit_and_push")
                    {
                        await ctx.EmitLogAsync($"Pushing {repo.RepoPath} → {repo.DeployBranch}…");
                        var pushResult = await _runner.RunAsync("git",
                            ["-C", repo.RepoPath, "push", "origin", repo.DeployBranch], null,
                            line => ctx.EmitLogAsync(line, "git"));
                        if (!pushResult.Success)
                            throw new InvalidOperationException($"git push failed in {repo.RepoPath}:\n{pushResult.StdErr}");
                    }
                }

                record.Status = BuildStatus.Running;
            }

            await ctx.StepCompletedAsync(2, "GitStatusCheck");
            await SaveStep();

            // Smart resume: if repos are clean and a previous build failed mid-pipeline with the
            // same versions, offer to skip steps that already completed successfully.
            int resumeFromStep = 0;
            if (dirtyRepos.Count == 0)
            {
                var recentBuilds = await _buildStore.QueryAsync(record.ProjectId, null, null, null, null, 1, 5);
                var candidate = recentBuilds
                    .Where(b => b.Id != record.Id && b.Status == BuildStatus.BuildFailed && b.FailedStep != null)
                    .Where(b => b.SucceededSteps.Contains("WriteVersionsAndTag"))
                    .Where(b => _stepNumbers.TryGetValue(b.FailedStep!, out var n) && n > 3)
                    .FirstOrDefault(b => b.Versions.Count == record.Versions.Count &&
                                         b.Versions.All(lv => record.Versions.Any(rv =>
                                             rv.ServiceName == lv.ServiceName && rv.NewVersion == lv.NewVersion)));

                if (candidate != null)
                {
                    var failedStepNum = _stepNumbers[candidate.FailedStep!];
                    await ctx.PauseAsync("smart_resume",
                        $"Last build failed at '{candidate.FailedStep}'. No code changes detected — resume from '{candidate.FailedStep}', skipping already-completed steps?",
                        ["resume", "start_fresh"]);
                    await SaveStep();

                    var tcsSr = new TaskCompletionSource<RespondRequest>();
                    _pauseWaiters[record.Id] = tcsSr;
                    var resumeResponse = await tcsSr.Task;

                    if (resumeResponse.Choice == "resume")
                    {
                        resumeFromStep = failedStepNum;
                        await ctx.EmitLogAsync($"Resuming from '{candidate.FailedStep}' — skipping {failedStepNum - 3} already-completed step(s)…", "shipright");
                    }
                    record.Status = BuildStatus.Running;
                }
            }

            // ── Step 3: Branch Check ──────────────────────────────────────────
            await ctx.StepStartedAsync(3, "BranchCheck");
            if (resumeFromStep > 3)
            {
                await ctx.EmitLogAsync("BranchCheck skipped — already completed in previous build", "shipright");
            }
            else
            {
            var wrongBranchRepos = new List<(string RepoPath, string CurrentBranch, string DeployBranch)>();
            foreach (var repo in project.GitRepos)
            {
                await ctx.EmitLogAsync($"Checking branch in {repo.RepoPath}…");
                var branchResult = await _runner.RunAsync("git",
                    ["-C", repo.RepoPath, "rev-parse", "--abbrev-ref", "HEAD"], null);
                if (!branchResult.Success)
                    throw new InvalidOperationException($"Could not determine branch in {repo.RepoPath}:\n{branchResult.StdErr}");
                var currentBranch = branchResult.StdOut.Trim();
                if (currentBranch != repo.DeployBranch)
                    wrongBranchRepos.Add((repo.RepoPath, currentBranch, repo.DeployBranch));
            }

            if (wrongBranchRepos.Count > 0)
            {
                var detail = string.Join(", ", wrongBranchRepos.Select(r => $"'{r.CurrentBranch}'→'{r.DeployBranch}'"));
                await ctx.PauseAsync("wrong_branch",
                    $"{wrongBranchRepos.Count} repo(s) on wrong branch ({detail}). What would you like to do?",
                    ["merge", "switch", "build_here", "abort"]);
                await SaveStep();

                var tcs = new TaskCompletionSource<RespondRequest>();
                _pauseWaiters[record.Id] = tcs;
                var response = await tcs.Task;

                if (response.Choice == "abort")
                {
                    record.Status = BuildStatus.Aborted;
                    await SaveStep();
                    await ctx.BuildCompletedAsync();
                    return;
                }

                if (response.Choice == "build_here")
                {
                    var branches = string.Join(", ", wrongBranchRepos.Select(r => $"'{r.CurrentBranch}'"));
                    await ctx.EmitLogAsync($"Building on current branch(es): {branches}", "shipright");
                }
                else
                {
                    foreach (var (repoPath, currentBranch, deployBranch) in wrongBranchRepos)
                    {
                        await ctx.EmitLogAsync($"Checking out '{deployBranch}' in {repoPath}…");
                        var checkout = await _runner.RunAsync("git",
                            ["-C", repoPath, "checkout", deployBranch], null,
                            line => ctx.EmitLogAsync(line, "git"));
                        if (!checkout.Success)
                            throw new InvalidOperationException($"git checkout failed in {repoPath}:\n{checkout.StdErr}");

                        await ctx.EmitLogAsync($"Pulling '{deployBranch}' in {repoPath}…");
                        var pull = await _runner.RunAsync("git",
                            ["-C", repoPath, "pull", "origin", deployBranch], null,
                            line => ctx.EmitLogAsync(line, "git"));
                        if (!pull.Success)
                            throw new InvalidOperationException($"git pull failed in {repoPath}:\n{pull.StdErr}");

                        if (response.Choice == "merge")
                        {
                            await ctx.EmitLogAsync($"Merging '{currentBranch}' into '{deployBranch}' in {repoPath}…", "git");
                            var merge = await _runner.RunAsync("git",
                                ["-C", repoPath, "merge", "--no-ff", currentBranch, "-m",
                                    $"chore: merge '{currentBranch}' into '{deployBranch}' for deployment"],
                                null, line => ctx.EmitLogAsync(line, "git"));

                            // Detect merge conflicts
                            if (!merge.Success)
                            {
                                var combinedOutput = merge.StdOut + merge.StdErr;
                                if (combinedOutput.Contains("CONFLICT"))
                                {
                                    await _runner.RunAsync("git", ["-C", repoPath, "merge", "--abort"], null);
                                    throw new InvalidOperationException(
                                        $"Merge conflicts in {repoPath}. Resolve manually on '{deployBranch}' then retry.");
                                }
                                throw new InvalidOperationException($"git merge failed in {repoPath}:\n{merge.StdErr}");
                            }

                            await ctx.EmitLogAsync($"Pushing '{deployBranch}' in {repoPath}…");
                            var pushMerge = await _runner.RunAsync("git",
                                ["-C", repoPath, "push", "origin", deployBranch], null,
                                line => ctx.EmitLogAsync(line, "git"));
                            if (!pushMerge.Success)
                                throw new InvalidOperationException($"git push after merge failed in {repoPath}:\n{pushMerge.StdErr}");
                        }
                    }
                }

                record.Status = BuildStatus.Running;
            }
            } // end else (not skipped)
            await ctx.StepCompletedAsync(3, "BranchCheck");
            await SaveStep();

            // ── Step 4: Write Versions & Tag ──────────────────────────────────
            await ctx.StepStartedAsync(4, "WriteVersionsAndTag");

            // Computed here so step 5 can use them even when step 4 is skipped
            var versionSummary = string.Join(", ", record.Versions.Select(v => $"{v.ServiceName} {v.NewVersion}"));
            var tag = BuildGitTag(record.Versions);
            record.GitTag = tag;

            if (resumeFromStep > 4)
            {
                await ctx.EmitLogAsync($"WriteVersionsAndTag skipped — already completed (tag: {tag})", "shipright");
            }
            else
            {
            var versionFilePaths = new List<string>();
            foreach (var sv in record.Versions)
            {
                var svc = project.Services.First(s => s.Name == sv.ServiceName);
                await VersionFileService.WriteAsync(svc.VersionFilePath, sv.NewVersion);
                versionFilePaths.Add(svc.VersionFilePath);
                await ctx.EmitLogAsync($"Wrote {sv.NewVersion} → {svc.VersionFilePath}", "shipright");
            }

            foreach (var repo in project.GitRepos)
            {
                var repoVersionFiles = versionFilePaths
                    .Where(vf => vf.StartsWith(repo.RepoPath, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (repoVersionFiles.Count == 0) continue;

                var addVersions = new[] { "-C", repo.RepoPath, "add" }
                    .Concat(repoVersionFiles).ToArray();
                var addResult2 = await _runner.RunAsync("git", addVersions, null,
                    line => ctx.EmitLogAsync(line, "git"));
                if (!addResult2.Success)
                    throw new InvalidOperationException($"git add versions failed in {repo.RepoPath}:\n{addResult2.StdErr}");

                var commitVersions = await _runner.RunAsync("git",
                    ["-C", repo.RepoPath, "commit", "-m", $"chore: bump versions — {versionSummary}"],
                    null, line => ctx.EmitLogAsync(line, "git"));
                if (!commitVersions.Success)
                {
                    var combinedOut = commitVersions.StdOut + commitVersions.StdErr;
                    if (combinedOut.Contains("nothing to commit"))
                        await ctx.EmitLogAsync($"Versions already committed in {repo.RepoPath} — skipping commit", "shipright");
                    else
                        throw new InvalidOperationException($"git commit versions failed in {repo.RepoPath}:\n{combinedOut}");
                }

                await ctx.EmitLogAsync($"Tagging {repo.RepoPath} as {tag}…");
                var tagResult = await _runner.RunAsync("git",
                    ["-C", repo.RepoPath, "tag", "-a", tag, "-m",
                        $"Build {DateTime.UtcNow:yyyy-MM-dd}: {versionSummary}"],
                    null, line => ctx.EmitLogAsync(line, "git"));
                if (!tagResult.Success)
                {
                    var tagOut = tagResult.StdOut + tagResult.StdErr;
                    if (tagOut.Contains("already exists"))
                        await ctx.EmitLogAsync($"Tag {tag} already exists in {repo.RepoPath} — skipping", "shipright");
                    else
                        throw new InvalidOperationException($"git tag failed in {repo.RepoPath}:\n{tagOut}");
                }

                await ctx.EmitLogAsync($"Pushing tag + branch to origin…");
                var pushSource = await _runner.RunAsync("git",
                    ["-C", repo.RepoPath, "push", "origin", repo.DeployBranch, "--follow-tags"],
                    null, line => ctx.EmitLogAsync(line, "git"), null, ct);
                if (!pushSource.Success)
                {
                    var pushOut = pushSource.StdOut + pushSource.StdErr;
                    if (pushOut.Contains("already exists"))
                        await ctx.EmitLogAsync($"Tag already pushed for {repo.RepoPath} — skipping", "shipright");
                    else
                        throw new InvalidOperationException($"git push failed in {repo.RepoPath}:\n{pushOut}");
                }
            }

            } // end else (not skipped)
            await ctx.StepCompletedAsync(4, "WriteVersionsAndTag");
            await SaveStep();

            // ── Step 5: Compose Repo Sync ─────────────────────────────────────
            await ctx.StepStartedAsync(5, "ComposeRepoSync");

            if (project.Server.DeployMode == DeployMode.FullyManaged)
            {
                await ctx.EmitLogAsync("ComposeRepoSync skipped — Fully Managed mode injects image tags at deploy time", "shipright");
            }
            else if (resumeFromStep > 5)
            {
                await ctx.EmitLogAsync("ComposeRepoSync skipped — already completed in previous build", "shipright");
            }
            else
            {
            var composeBranch = project.GitRepos.FirstOrDefault()?.DeployBranch ?? "master";
            await ctx.EmitLogAsync("Pulling compose repo…");
            var composePull = await _runner.RunAsync("git",
                ["-C", project.Wsl.WorkingDir, "pull", "origin", composeBranch],
                null, line => ctx.EmitLogAsync(line, "git"));
            if (!composePull.Success)
                throw new InvalidOperationException($"git pull compose repo failed:\n{composePull.StdErr}");

            // Update docker-compose.yml image tags
            var composePath = Path.Combine(project.Wsl.WorkingDir, "docker-compose.yml");
            if (File.Exists(composePath))
            {
                var imageMap = record.Versions.ToDictionary(
                    v => v.DockerImageName,
                    v => v.NewVersion);
                await DockerComposeUpdater.UpdateAsync(composePath, imageMap);
                await ctx.EmitLogAsync($"Updated docker-compose.yml with new image tags", "shipright");

                var addCompose = await _runner.RunAsync("git",
                    ["-C", project.Wsl.WorkingDir, "add", "docker-compose.yml"],
                    null, line => ctx.EmitLogAsync(line, "git"));
                if (!addCompose.Success)
                    throw new InvalidOperationException($"git add compose failed:\n{addCompose.StdErr}");

                var commitCompose = await _runner.RunAsync("git",
                    ["-C", project.Wsl.WorkingDir, "commit", "-m", $"chore: deploy — {versionSummary}"],
                    null, line => ctx.EmitLogAsync(line, "git"));
                if (!commitCompose.Success)
                {
                    var composeCommitOut = commitCompose.StdOut + commitCompose.StdErr;
                    if (composeCommitOut.Contains("nothing to commit"))
                        await ctx.EmitLogAsync("Compose already committed — skipping", "shipright");
                    else
                        throw new InvalidOperationException($"git commit compose failed:\n{commitCompose.StdErr}");
                }

                await ctx.EmitLogAsync("Pushing compose repo…");
                var pushCompose = await _runner.RunAsync("git",
                    ["-C", project.Wsl.WorkingDir, "push", "origin", composeBranch],
                    null, line => ctx.EmitLogAsync(line, "git"));
                if (!pushCompose.Success)
                    throw new InvalidOperationException($"git push compose repo failed:\n{pushCompose.StdErr}");
            }
            else
            {
                await ctx.EmitLogAsync($"docker-compose.yml not found at {composePath} — skipping compose sync", "shipright");
            }
            } // end else (not skipped)
            await ctx.StepCompletedAsync(5, "ComposeRepoSync");
            await SaveStep();

            // ── Step 6: Docker Build ──────────────────────────────────────────
            await RunDockerBuildAsync(ctx, record, project, SaveStep, ct);

            // ── Step 7: Build Complete ────────────────────────────────────────
            await ctx.StepStartedAsync(7, "BuildComplete");
            record.Status = BuildStatus.ImageBuilt;
            record.CompletedAt = DateTime.UtcNow;
            await ctx.StepCompletedAsync(7, "BuildComplete");
            await SaveStep();
            await ctx.BuildCompletedAsync();

            Log.Information("Build {BuildId} completed: {Status}, tag: {Tag}",
                record.Id, record.Status, record.GitTag);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Log.Warning("Build {BuildId} cancelled at step {Step}", record.Id, record.CurrentStepName);
            record.Status = BuildStatus.Aborted;
            record.FailedStep = record.CurrentStepName;
            record.ErrorSummary = "Cancelled by user.";
            record.CompletedAt = DateTime.UtcNow;
            try { await _buildStore.SaveAsync(record); await ctx.EmitLogAsync("Build cancelled.", "shipright"); } catch { }
            await ctx.BuildCompletedAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Build {BuildId} failed at step {Step}", record.Id, record.CurrentStepName);
            record.Status = BuildStatus.BuildFailed;
            record.FailedStep = record.CurrentStepName;
            record.ErrorSummary = ex.Message;
            record.CompletedAt = DateTime.UtcNow;
            try
            {
                await _buildStore.SaveAsync(record);
                await ctx.EmitLogAsync($"[ERROR] {ex.Message}", "shipright");
            }
            catch (Exception saveEx)
            {
                Log.Error(saveEx, "Failed to persist failed-build record {BuildId}", record.Id);
            }
            await ctx.BuildCompletedAsync();
        }
        finally
        {
            _cancellations.TryRemove(record.Id, out _);
        }
    }

    public async Task PushAsync(string buildId)
    {
        var record = await _buildStore.GetByIdAsync(buildId);
        if (record is null) return;

        var project = await _projectStore.GetByIdAsync(record.ProjectId);
        if (project is null) return;

        using var _ = LogContext.PushProperty("BuildId", buildId);
        var ctx = new PipelineContext(record, _bus);

        record.Status = BuildStatus.Running;
        await _buildStore.SaveAsync(record);

        using var cts = new CancellationTokenSource();
        _cancellations[buildId] = cts;
        var ct = cts.Token;

        try
        {
            await ctx.EmitLogAsync("Push pipeline started.", "shipright");

            // ── Push Step 1: Docker Login Check ───────────────────────────────
            await ctx.StepStartedAsync(1, "DockerLoginCheck");

            bool needsLogin = true;
            if (OperatingSystem.IsWindows())
            {
                // On Windows, docker runs in WSL — read config from WSL home
                var cfgResult = await _runner.RunAsync("wsl",
                    ["sh", "-c", "cat ~/.docker/config.json 2>/dev/null || echo '{}'"],
                    null);
                if (cfgResult.Success)
                {
                    var cfg = cfgResult.StdOut;
                    needsLogin = !cfg.Contains("docker.io") && !cfg.Contains("index.docker.io");
                }
            }
            else
            {
                var dockerConfigPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".docker", "config.json");
                if (File.Exists(dockerConfigPath))
                {
                    var cfg = await File.ReadAllTextAsync(dockerConfigPath);
                    needsLogin = !cfg.Contains("docker.io") && !cfg.Contains("index.docker.io");
                }
            }

            if (needsLogin)
            {
                var loggedIn = await DockerLoginAsync(ctx, record, "Docker Hub credentials required.");
                if (!loggedIn)
                {
                    record.Status = BuildStatus.Aborted;
                    await _buildStore.SaveAsync(record);
                    await ctx.PushCompletedAsync();
                    return;
                }
            }
            else
            {
                await ctx.EmitLogAsync("Docker Hub credentials found — skipping login.", "shipright");
            }

            await ctx.StepCompletedAsync(1, "DockerLoginCheck");
            await _buildStore.SaveAsync(record);

            // ── Push Step 2: Docker Push ──────────────────────────────────────
            await ctx.StepStartedAsync(2, "DockerPush");

            foreach (var sv in record.Versions)
            {
                var svc = project.Services.First(s => s.Name == sv.ServiceName);

                var pushResult = await _runner.RunAsync("docker",
                    ["push", $"{svc.DockerImageName}:{sv.NewVersion}"],
                    null,
                    line => ctx.EmitLogAsync(line, "docker"),
                    line => ctx.EmitLogAsync(line, "docker"),
                    ct);

                if (!pushResult.Success)
                {
                    var pushOut = pushResult.StdOut + pushResult.StdErr;
                    if (pushOut.Contains("denied") || pushOut.Contains("unauthorized"))
                    {
                        // Credentials rejected — give the user a chance to re-authenticate
                        await ctx.EmitLogAsync("Push rejected — Docker Hub credentials required.", "shipright");
                        var loggedIn = await DockerLoginAsync(ctx, record, "Docker Hub access denied. Re-enter credentials.");
                        if (!loggedIn)
                        {
                            record.Status = BuildStatus.Aborted;
                            await _buildStore.SaveAsync(record);
                            await ctx.PushCompletedAsync();
                            return;
                        }
                        // Retry once after fresh login
                        var retry = await _runner.RunAsync("docker",
                            ["push", $"{svc.DockerImageName}:{sv.NewVersion}"],
                            null,
                            line => ctx.EmitLogAsync(line, "docker"),
                            line => ctx.EmitLogAsync(line, "docker"),
                            ct);
                        if (!retry.Success)
                            throw new InvalidOperationException($"docker push failed for {svc.Name} after re-login (exit {retry.ExitCode}).");
                    }
                    else
                    {
                        throw new InvalidOperationException($"docker push {sv.NewVersion} failed for {svc.Name} (exit {pushResult.ExitCode}).");
                    }
                }

                await ctx.EmitLogAsync($"Pushed {svc.DockerImageName}:{sv.NewVersion}", "shipright");
            }

            await ctx.StepCompletedAsync(2, "DockerPush");

            // ── Push Step 3: Push Complete ────────────────────────────────────
            await ctx.StepStartedAsync(3, "PushComplete");
            record.Status = BuildStatus.PushSucceeded;
            await ctx.StepCompletedAsync(3, "PushComplete");
            await _buildStore.SaveAsync(record);
            await ctx.PushCompletedAsync();

            Log.Information("Push {BuildId} completed: {Status}", record.Id, record.Status);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Log.Warning("Push {BuildId} cancelled", record.Id);
            record.Status = BuildStatus.Aborted;
            record.ErrorSummary = "Cancelled by user.";
            try { await _buildStore.SaveAsync(record); await ctx.EmitLogAsync("Push cancelled.", "shipright"); } catch { }
            await ctx.PushCompletedAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Push {BuildId} failed at step {Step}", record.Id, record.CurrentStepName);
            record.Status = BuildStatus.PushFailed;
            record.FailedStep = record.CurrentStepName;
            record.ErrorSummary = ex.Message;
            try
            {
                await _buildStore.SaveAsync(record);
                await ctx.EmitLogAsync($"[ERROR] {ex.Message}", "shipright");
            }
            catch (Exception saveEx)
            {
                Log.Error(saveEx, "Failed to persist failed-push record {BuildId}", record.Id);
            }
            await ctx.PushCompletedAsync();
        }
        finally
        {
            _cancellations.TryRemove(buildId, out var _unused);
        }
    }

    protected virtual async Task RunDockerBuildAsync(PipelineContext ctx, BuildRecord record,
        ProjectConfig project, Func<Task> save, CancellationToken ct = default)
    {
        // ── Step 6: Docker Build (per service) ───────────────────────────────
        await ctx.StepStartedAsync(6, "DockerBuild");

        // Check which images already exist locally with the correct tag
        var alreadyBuilt = new HashSet<string>();
        foreach (var sv in record.Versions)
        {
            var svc = project.Services.First(s => s.Name == sv.ServiceName);
            var tag = $"{svc.DockerImageName}:{sv.NewVersion}";
            await ctx.EmitLogAsync($"Checking if {tag} exists locally…", "shipright");
            var inspectResult = await _runner.RunAsync("docker",
                ["image", "inspect", "--format", "{{.Id}}", tag],
                null, null, null, ct);
            if (inspectResult.Success)
            {
                alreadyBuilt.Add(svc.DockerImageName);
                await ctx.EmitLogAsync($"  → {tag} already exists in local Docker storage", "shipright");
            }
        }

        var toSkip = new HashSet<string>();
        if (alreadyBuilt.Count > 0)
        {
            await ctx.PauseAsync("image_exists",
                $"{alreadyBuilt.Count} image(s) already exist locally with the correct tag. Choose which to skip rebuilding:",
                [],
                checkboxes: alreadyBuilt.ToArray());
            await save();
            var tcsImg = new TaskCompletionSource<RespondRequest>();
            _pauseWaiters[record.Id] = tcsImg;
            var imgResponse = await tcsImg.Task;
            toSkip = imgResponse.Data?
                .Where(kv => kv.Value == "true")
                .Select(kv => kv.Key)
                .ToHashSet() ?? new HashSet<string>();
            record.Status = BuildStatus.Running;
        }

        foreach (var sv in record.Versions)
        {
            var svc = project.Services.First(s => s.Name == sv.ServiceName);

            if (toSkip.Contains(svc.DockerImageName))
            {
                await ctx.EmitLogAsync($"Skipped {svc.DockerImageName}:{sv.NewVersion} — already built", "shipright");
                continue;
            }

            await ctx.EmitLogAsync($"Building {svc.DockerImageName}:{sv.NewVersion}…", "shipright");

            var buildArgs = new List<string> { "build", "--progress=plain" };
            if (!string.IsNullOrEmpty(sv.PreviousVersion))
                buildArgs.AddRange(["--cache-from", $"{svc.DockerImageName}:{sv.PreviousVersion}"]);
            buildArgs.AddRange(["--build-arg", "BUILDKIT_INLINE_CACHE=1",
                                 "-t", $"{svc.DockerImageName}:{sv.NewVersion}",
                                 svc.BuildContextPath]);

            var buildResult = await _runner.RunAsync("docker",
                buildArgs.ToArray(),
                null,
                line => ctx.EmitLogAsync(line, "docker"),
                line => ctx.EmitLogAsync(line, "docker"),
                ct,
                envOverride: new Dictionary<string, string> { ["DOCKER_BUILDKIT"] = "1" });

            if (!buildResult.Success)
            {
                await ctx.StepCompletedAsync(6, "DockerBuild", success: false);
                throw new InvalidOperationException(
                    $"docker build failed for {svc.Name} (exit {buildResult.ExitCode}).");
            }

            await ctx.EmitLogAsync($"Built {svc.DockerImageName}:{sv.NewVersion}", "shipright");
        }

        await ctx.StepCompletedAsync(6, "DockerBuild");
        await save();
    }

    private static string BuildGitTag(List<ServiceVersion> versions)
    {
        if (versions.Count == 2)
        {
            // Convention: b_{firstVer}_f_{secondVer}
            return $"b_{versions[0].NewVersion}_f_{versions[1].NewVersion}";
        }
        return $"ship_{DateTime.UtcNow:yyyyMMddHHmm}";
    }

    // Prompts for Docker Hub credentials and performs docker login.
    // Returns true on success, false if the user chose to abort.
    private async Task<bool> DockerLoginAsync(PipelineContext ctx, BuildRecord record, string prompt)
    {
        await ctx.PauseAsync("docker_login_required", prompt, ["login", "abort"],
            new[] { "username", "password" });
        await _buildStore.SaveAsync(record);

        var tcs = new TaskCompletionSource<RespondRequest>();
        _pauseWaiters[record.Id] = tcs;
        var response = await tcs.Task;

        if (response.Choice == "abort") return false;

        var username = response.Data?.GetValueOrDefault("username") ?? "";
        var password = response.Data?.GetValueOrDefault("password") ?? "";

        var (loginExe, loginArgs) = ProcessRunner.ResolveForPlatform(
            "docker", ["login", "-u", username, "--password-stdin"]);
        using var loginProc = new global::System.Diagnostics.Process
        {
            StartInfo = new global::System.Diagnostics.ProcessStartInfo
            {
                FileName = loginExe,
                RedirectStandardInput = true, RedirectStandardOutput = true,
                RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            }
        };
        foreach (var a in loginArgs) loginProc.StartInfo.ArgumentList.Add(a);
        loginProc.Start();
        await loginProc.StandardInput.WriteLineAsync(password);
        loginProc.StandardInput.Close();
        var loginOut = await loginProc.StandardOutput.ReadToEndAsync();
        var loginErr = await loginProc.StandardError.ReadToEndAsync();
        await loginProc.WaitForExitAsync();
        await ctx.EmitLogAsync($"docker login: {(loginOut + loginErr).Trim()}", "docker");

        if (loginProc.ExitCode != 0)
            throw new InvalidOperationException("Docker login failed — check username and password.");

        record.Status = BuildStatus.Running;
        return true;
    }

    private static string BuildSemiManagedDeployCmd(ProjectConfig project)
    {
        var branch = project.GitRepos.FirstOrDefault()?.DeployBranch ?? "master";
        return $"cd {project.Server.RemoteWorkingDir} && git pull origin {branch} && docker compose pull && docker compose up -d";
    }

    private static string BuildFullyManagedDeployCmd(ProjectConfig project, List<ServiceVersion> versions)
    {
        var envVars = string.Join(" ", versions.Select(v =>
        {
            var key = Regex.Replace(v.ServiceName.ToUpperInvariant(), @"[^A-Z0-9]", "_") + "_TAG";
            return $"{key}={v.DockerImageName}:{v.NewVersion}";
        }));
        return $"cd {project.Server.RemoteWorkingDir} && export {envVars} && docker compose pull && docker compose up -d";
    }

    public async Task<string> RollbackAsync(string targetBuildId)
    {
        var target = await _buildStore.GetByIdAsync(targetBuildId)
            ?? throw new InvalidOperationException("Build record not found.");
        var project = await _projectStore.GetByIdAsync(target.ProjectId)
            ?? throw new InvalidOperationException("Project not found.");

        if (project.Server.DeployMode == DeployMode.Unmanaged)
            throw new InvalidOperationException("Rollback requires Semi-managed or Fully Managed deploy mode.");

        var record = new BuildRecord
        {
            ProjectId             = project.Id,
            ProjectName           = project.Name,
            GitTag                = target.GitTag,
            Versions              = target.Versions.ToList(),
            IsRollback            = true,
            RolledBackFromBuildId = targetBuildId,
            Status                = BuildStatus.Deploying,
        };
        await _buildStore.SaveAsync(record);
        _bus.Register(record.Id);
        _ = Task.Run(() => ExecuteRollbackAsync(project, record, target, CancellationToken.None));
        return record.Id;
    }

    private async Task ExecuteRollbackAsync(ProjectConfig project, BuildRecord record,
        BuildRecord target, CancellationToken ct)
    {
        var ctx = new PipelineContext(record, _bus);
        try
        {
            if (project.Server.DeployMode == DeployMode.SemiManaged)
            {
                var branch = project.GitRepos.FirstOrDefault()?.DeployBranch ?? "master";
                await ctx.EmitLogAsync("Updating compose repo to rollback versions…", "shipright");
                await _runner.RunAsync("git",
                    ["-C", project.Wsl.WorkingDir, "pull", "origin", branch],
                    null, line => ctx.EmitLogAsync(line, "git"));
                var composePath = Path.Combine(project.Wsl.WorkingDir, "docker-compose.yml");
                var imageMap = target.Versions.ToDictionary(v => v.DockerImageName, v => v.NewVersion);
                await DockerComposeUpdater.UpdateAsync(composePath, imageMap);
                await _runner.RunAsync("git",
                    ["-C", project.Wsl.WorkingDir, "add", "docker-compose.yml"],
                    null, line => ctx.EmitLogAsync(line, "git"));
                var commitResult = await _runner.RunAsync("git",
                    ["-C", project.Wsl.WorkingDir, "commit", "-m", $"chore: rollback to {target.GitTag}"],
                    null, line => ctx.EmitLogAsync(line, "git"));
                if (!commitResult.Success)
                {
                    var commitOut = commitResult.StdOut + commitResult.StdErr;
                    if (!commitOut.Contains("nothing to commit"))
                        throw new InvalidOperationException($"git commit failed: {commitOut}");
                    await ctx.EmitLogAsync("Compose already at target versions — skipping commit", "shipright");
                }
                await _runner.RunAsync("git",
                    ["-C", project.Wsl.WorkingDir, "push", "origin", branch],
                    null, line => ctx.EmitLogAsync(line, "git"));
            }

            await ctx.EmitLogAsync($"Connecting to {project.Server.Host}…", "ssh");
            var cmd = project.Server.DeployMode == DeployMode.FullyManaged
                ? BuildFullyManagedDeployCmd(project, target.Versions)
                : BuildSemiManagedDeployCmd(project);

            var exit = await _ssh.RunAsync(
                project.Server.Host, project.Server.Username,
                project.Server.SshKeyPath, cmd,
                line => ctx.EmitLogAsync(line, "ssh"), ct: ct);

            record.Status     = exit == 0 ? BuildStatus.Deployed : BuildStatus.DeployFailed;
            record.DeployedAt = exit == 0 ? DateTime.UtcNow : null;
            if (exit != 0) record.ErrorSummary = $"Rollback command exited with code {exit}.";
        }
        catch (Exception ex)
        {
            record.Status       = BuildStatus.DeployFailed;
            record.ErrorSummary = ex.Message;
            Log.Error(ex, "Rollback failed for project {ProjectId}", project.Id);
            await ctx.EmitLogAsync($"[ERROR] {ex.Message}", "shipright");
        }
        finally
        {
            await _buildStore.SaveAsync(record);
            await ctx.DeployCompletedAsync();
        }
    }

    private static string TryReadVersion(string path)
    {
        try { return File.ReadAllText(path).Trim(); }
        catch { return ""; }
    }
}
