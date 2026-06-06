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

        try
        {
            await ctx.EmitLogAsync($"Connecting to {project.Server.Username}@{project.Server.Host}…", "ssh");

            var cmd = $"cd {project.Server.RemoteWorkingDir} && bash {project.Server.RebuildScript}";
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

        try
        {
            Log.Information("Build {BuildId} pipeline started", record.Id);

            // ── Step 1: Precondition Check ────────────────────────────────────
            await ctx.StepStartedAsync(1, "PreconditionCheck");
            foreach (var svc in project.Services)
            {
                if (!File.Exists(svc.VersionFilePath))
                    throw new InvalidOperationException($"version.txt not found: {svc.VersionFilePath}");
                if (!Directory.Exists(svc.BuildContextPath))
                    throw new InvalidOperationException($"Build context not found: {svc.BuildContextPath}");
            }
            foreach (var repo in project.GitRepos)
                if (!Directory.Exists(repo.RepoPath))
                    throw new InvalidOperationException($"Git repo not found: {repo.RepoPath}");

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
                    var addResult = await _runner.RunAsync("git",
                        ["-C", repo.RepoPath, "add", "-A"], null,
                        line => ctx.EmitLogAsync(line, "git"));
                    if (!addResult.Success)
                        throw new InvalidOperationException($"git add failed in {repo.RepoPath}:\n{addResult.StdErr}");

                    var commitResult = await _runner.RunAsync("git",
                        ["-C", repo.RepoPath, "commit", "-m", msg], null,
                        line => ctx.EmitLogAsync(line, "git"));
                    if (!commitResult.Success)
                        throw new InvalidOperationException($"git commit failed in {repo.RepoPath}:\n{commitResult.StdErr}");

                    if (response.Choice == "commit_and_push")
                    {
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

            // ── Step 3: Branch Check ──────────────────────────────────────────
            await ctx.StepStartedAsync(3, "BranchCheck");
            var wrongBranchRepos = new List<(string RepoPath, string CurrentBranch, string DeployBranch)>();
            foreach (var repo in project.GitRepos)
            {
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
                    $"{wrongBranchRepos.Count} repo(s) on wrong branch ({detail}). Merge into deploy branch, or just switch?",
                    ["merge", "switch", "abort"]);
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

                foreach (var (repoPath, currentBranch, deployBranch) in wrongBranchRepos)
                {
                    var checkout = await _runner.RunAsync("git",
                        ["-C", repoPath, "checkout", deployBranch], null,
                        line => ctx.EmitLogAsync(line, "git"));
                    if (!checkout.Success)
                        throw new InvalidOperationException($"git checkout failed in {repoPath}:\n{checkout.StdErr}");

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
                        if (!merge.Success)
                            throw new InvalidOperationException($"git merge failed in {repoPath}:\n{merge.StdErr}");

                        var pushMerge = await _runner.RunAsync("git",
                            ["-C", repoPath, "push", "origin", deployBranch], null,
                            line => ctx.EmitLogAsync(line, "git"));
                        if (!pushMerge.Success)
                            throw new InvalidOperationException($"git push after merge failed in {repoPath}:\n{pushMerge.StdErr}");
                    }
                }

                record.Status = BuildStatus.Running;
            }

            await ctx.StepCompletedAsync(3, "BranchCheck");
            await SaveStep();

            // ── Step 4: Write Versions & Tag ──────────────────────────────────
            await ctx.StepStartedAsync(4, "WriteVersionsAndTag");

            var versionSummary = string.Join(", ", record.Versions
                .Select(v => $"{v.ServiceName} {v.NewVersion}"));

            // Write version.txt for each service
            var versionFilePaths = new List<string>();
            foreach (var sv in record.Versions)
            {
                var svc = project.Services.First(s => s.Name == sv.ServiceName);
                await VersionFileService.WriteAsync(svc.VersionFilePath, sv.NewVersion);
                versionFilePaths.Add(svc.VersionFilePath);
                await ctx.EmitLogAsync($"Wrote {sv.NewVersion} → {svc.VersionFilePath}", "shipright");
            }

            // Commit version bumps per repo, then tag and push each
            var tag = BuildGitTag(record.Versions);
            record.GitTag = tag;

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
                    throw new InvalidOperationException($"git commit versions failed in {repo.RepoPath}:\n{commitVersions.StdErr}");

                var tagResult = await _runner.RunAsync("git",
                    ["-C", repo.RepoPath, "tag", "-a", tag, "-m",
                        $"Build {DateTime.UtcNow:yyyy-MM-dd}: {versionSummary}"],
                    null, line => ctx.EmitLogAsync(line, "git"));
                if (!tagResult.Success)
                    throw new InvalidOperationException($"git tag failed in {repo.RepoPath}:\n{tagResult.StdErr}");

                var pushSource = await _runner.RunAsync("git",
                    ["-C", repo.RepoPath, "push", "origin", repo.DeployBranch, "--follow-tags"],
                    null, line => ctx.EmitLogAsync(line, "git"));
                if (!pushSource.Success)
                    throw new InvalidOperationException($"git push failed in {repo.RepoPath}:\n{pushSource.StdErr}");
            }

            await ctx.StepCompletedAsync(4, "WriteVersionsAndTag");
            await SaveStep();

            // ── Step 5: Compose Repo Sync ─────────────────────────────────────
            await ctx.StepStartedAsync(5, "ComposeRepoSync");

            var composeBranch = project.GitRepos.FirstOrDefault()?.DeployBranch ?? "master";
            // Pull compose repo first (get teammates' changes before modifying)
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
                    throw new InvalidOperationException($"git commit compose failed:\n{commitCompose.StdErr}");

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

            await ctx.StepCompletedAsync(5, "ComposeRepoSync");
            await SaveStep();

            // ── Steps 6–7: Docker (Slice 5) ───────────────────────────────────
            await RunDockerStepsAsync(ctx, record, project, SaveStep);

            // ── Step 8: Build Complete ────────────────────────────────────────
            await ctx.StepStartedAsync(8, "BuildComplete");
            record.Status = BuildStatus.BuildSucceeded;
            record.CompletedAt = DateTime.UtcNow;
            await ctx.StepCompletedAsync(8, "BuildComplete");
            await SaveStep();
            await ctx.BuildCompletedAsync();

            Log.Information("Build {BuildId} completed: {Status}, tag: {Tag}",
                record.Id, record.Status, record.GitTag);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Build {BuildId} failed at step {Step}", record.Id, record.CurrentStepName);
            record.Status = BuildStatus.BuildFailed;
            record.FailedStep = record.CurrentStepName;
            record.ErrorSummary = ex.Message;
            record.CompletedAt = DateTime.UtcNow;
            await _buildStore.SaveAsync(record);
            await ctx.EmitLogAsync($"[ERROR] {ex.Message}", "shipright");
            await ctx.BuildCompletedAsync();
        }
    }

    protected virtual async Task RunDockerStepsAsync(PipelineContext ctx, BuildRecord record,
        ProjectConfig project, Func<Task> save)
    {
        // ── Step 6: Docker Login Check ────────────────────────────────────────
        await ctx.StepStartedAsync(6, "DockerLoginCheck");

        var dockerConfigPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".docker", "config.json");

        bool needsLogin = true;
        if (File.Exists(dockerConfigPath))
        {
            var cfg = await File.ReadAllTextAsync(dockerConfigPath);
            needsLogin = !cfg.Contains("docker.io") && !cfg.Contains("index.docker.io");
        }

        if (needsLogin)
        {
            await ctx.PauseAsync("docker_login_required",
                "Docker Hub credentials required.",
                ["login", "abort"],
                new[] { "username", "password" });
            await save();

            var tcs = new TaskCompletionSource<RespondRequest>();
            _pauseWaiters[record.Id] = tcs;
            var response = await tcs.Task;

            if (response.Choice == "abort")
            {
                record.Status = BuildStatus.Aborted;
                await save();
                await ctx.BuildCompletedAsync();
                return;
            }

            var username = response.Data?.GetValueOrDefault("username") ?? "";
            var password = response.Data?.GetValueOrDefault("password") ?? "";

            // Pass password via stdin — never via command line args
            var loginProc = new global::System.Diagnostics.Process
            {
                StartInfo = new global::System.Diagnostics.ProcessStartInfo
                {
                    FileName = "docker",
                    Arguments = $"login -u {username} --password-stdin",
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }
            };
            loginProc.Start();
            await loginProc.StandardInput.WriteLineAsync(password);
            loginProc.StandardInput.Close();
            var loginOut = await loginProc.StandardOutput.ReadToEndAsync();
            await loginProc.WaitForExitAsync();

            await ctx.EmitLogAsync($"docker login: {loginOut.Trim()}", "docker");
            if (loginProc.ExitCode != 0)
                throw new InvalidOperationException("Docker login failed.");

            record.Status = BuildStatus.Running;
        }
        else
        {
            await ctx.EmitLogAsync("Docker Hub credentials found — skipping login.", "shipright");
        }

        await ctx.StepCompletedAsync(6, "DockerLoginCheck");
        await save();

        // ── Step 7: Docker Build & Push (per service) ─────────────────────────
        await ctx.StepStartedAsync(7, "DockerBuildAndPush");

        foreach (var sv in record.Versions)
        {
            var svc = project.Services.First(s => s.Name == sv.ServiceName);

            await ctx.EmitLogAsync($"Building {svc.DockerImageName}:{sv.NewVersion}…", "shipright");

            var buildResult = await _runner.RunAsync("docker",
                ["build",
                 "-t", $"{svc.DockerImageName}:{sv.NewVersion}",
                 "-t", $"{svc.DockerImageName}:latest",
                 svc.BuildContextPath],
                null,
                line => ctx.EmitLogAsync(line, "docker"),
                line => ctx.EmitLogAsync(line, "docker"));

            if (!buildResult.Success)
                throw new InvalidOperationException(
                    $"docker build failed for {svc.Name} (exit {buildResult.ExitCode}).");

            var pushVersion = await _runner.RunAsync("docker",
                ["push", $"{svc.DockerImageName}:{sv.NewVersion}"],
                null,
                line => ctx.EmitLogAsync(line, "docker"),
                line => ctx.EmitLogAsync(line, "docker"));
            if (!pushVersion.Success)
                throw new InvalidOperationException($"docker push {sv.NewVersion} failed for {svc.Name}.");

            var pushLatest = await _runner.RunAsync("docker",
                ["push", $"{svc.DockerImageName}:latest"],
                null,
                line => ctx.EmitLogAsync(line, "docker"),
                line => ctx.EmitLogAsync(line, "docker"));
            if (!pushLatest.Success)
                throw new InvalidOperationException($"docker push latest failed for {svc.Name}.");

            await ctx.EmitLogAsync($"Pushed {svc.DockerImageName}:{sv.NewVersion} + :latest", "shipright");
        }

        await ctx.StepCompletedAsync(7, "DockerBuildAndPush");
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

    private static string TryReadVersion(string path)
    {
        try { return File.ReadAllText(path).Trim(); }
        catch { return ""; }
    }
}
