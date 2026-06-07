using Serilog;
using ShipRight.Modules.Projects;
using ShipRight.Shared.Events;
using ShipRight.Shared.ProcessRunner;

namespace ShipRight.Modules.RepoMaintenance;

public static class RepoMaintenanceRouter
{
    public static void MapRepoMaintenanceRoutes(this WebApplication app)
    {
        // ── Purge history ────────────────────────────────────────────────────
        // Rewrites git history to permanently remove files matching given patterns.
        // Requires git-filter-repo (pip install git-filter-repo) on the WSL system.
        // After history rewrite all collaborators must re-clone.

        app.MapPost("/api/projects/{id}/repos/{repoIndex}/purge-history", async (
            string id, int repoIndex, PurgeHistoryRequest request,
            IProjectStore store, BuildEventBus bus, IProcessRunner runner) =>
        {
            var project = await store.GetByIdAsync(id);
            if (project is null)
                return Results.NotFound(new { isError = true, message = $"Project '{id}' not found." });

            if (repoIndex < 0 || repoIndex >= project.GitRepos.Count)
                return Results.BadRequest(new { isError = true, message = $"Repo index {repoIndex} is out of range." });

            if (request.Patterns is null || request.Patterns.Count == 0)
                return Results.BadRequest(new { isError = true, message = "At least one pattern is required." });

            var opId = Guid.NewGuid().ToString("N");
            bus.Register(opId);
            _ = Task.Run(() => RunPurgeHistoryAsync(project, repoIndex, request.Patterns, opId, bus, runner));
            return Results.Accepted($"/api/repos/ops/{opId}/stream",
                new { opId, message = "History purge started." });
        });

        // ── Delete files ─────────────────────────────────────────────────────
        // Normal git rm + commit + push — does not rewrite history.

        app.MapPost("/api/projects/{id}/repos/{repoIndex}/delete-files", async (
            string id, int repoIndex, DeleteFilesRequest request,
            IProjectStore store, BuildEventBus bus, IProcessRunner runner) =>
        {
            var project = await store.GetByIdAsync(id);
            if (project is null)
                return Results.NotFound(new { isError = true, message = $"Project '{id}' not found." });

            if (repoIndex < 0 || repoIndex >= project.GitRepos.Count)
                return Results.BadRequest(new { isError = true, message = $"Repo index {repoIndex} is out of range." });

            if (request.Files is null || request.Files.Count == 0)
                return Results.BadRequest(new { isError = true, message = "At least one file path is required." });

            var opId = Guid.NewGuid().ToString("N");
            bus.Register(opId);
            _ = Task.Run(() => RunDeleteFilesAsync(project, repoIndex, request.Files, opId, bus, runner));
            return Results.Accepted($"/api/repos/ops/{opId}/stream",
                new { opId, message = "File deletion started." });
        });

        // ── SSE stream ────────────────────────────────────────────────────────

        app.MapGet("/api/repos/ops/{opId}/stream", async (
            string opId, BuildEventBus bus, HttpContext http, CancellationToken ct) =>
        {
            http.Response.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            http.Response.Headers["X-Accel-Buffering"] = "no";
            http.Response.Headers.Connection = "keep-alive";

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var heartbeat = Task.Run(async () =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(15), cts.Token);
                        await http.Response.WriteAsync(": heartbeat\n\n", cts.Token);
                        await http.Response.Body.FlushAsync(cts.Token);
                    }
                    catch (OperationCanceledException) { break; }
                }
            });

            var reader = bus.Subscribe(opId);
            try
            {
                await foreach (var payload in reader.ReadAllAsync(cts.Token))
                {
                    await http.Response.WriteAsync($"data: {payload}\n\n", cts.Token);
                    await http.Response.Body.FlushAsync(cts.Token);
                }
            }
            catch (OperationCanceledException) { /* client disconnected */ }
            finally
            {
                await cts.CancelAsync();
                await heartbeat;
                bus.Unsubscribe(opId, reader);
            }
        });
    }

    private static async Task RunPurgeHistoryAsync(
        ProjectConfig project, int repoIndex, List<string> patterns,
        string opId, BuildEventBus bus, IProcessRunner runner)
    {
        var repo = project.GitRepos[repoIndex];
        async Task Log(string msg) => await bus.EmitAsync(opId, "log", new { message = msg });

        try
        {
            await Log($"Purging history from {repo.RepoPath}…");
            await Log($"Patterns: {string.Join(", ", patterns)}");

            // Build args: git filter-repo [--path p1 --invert-paths] [--path p2 --invert-paths] ...
            // filter-repo uses --path once per pattern; --invert-paths applies to all preceding --path args
            var filterArgs = new List<string> { "-C", repo.RepoPath, "filter-repo" };
            foreach (var pattern in patterns)
            {
                filterArgs.Add("--path");
                filterArgs.Add(pattern);
            }
            filterArgs.Add("--invert-paths");
            filterArgs.Add("--force");

            await Log($"Running: git {string.Join(' ', filterArgs)}");
            var result = await runner.RunAsync("git", filterArgs.ToArray(), null,
                line => bus.EmitAsync(opId, "log", new { message = line }),
                line => bus.EmitAsync(opId, "log", new { message = line }));

            if (!result.Success)
                throw new InvalidOperationException(
                    $"git filter-repo failed (exit {result.ExitCode}). " +
                    "Is git-filter-repo installed? Run: pip install git-filter-repo");

            // Force push to update remote
            await Log($"Force-pushing to origin/{repo.DeployBranch}…");
            var pushResult = await runner.RunAsync("git",
                ["-C", repo.RepoPath, "push", "--force", "origin", repo.DeployBranch],
                null,
                line => bus.EmitAsync(opId, "log", new { message = line }),
                line => bus.EmitAsync(opId, "log", new { message = line }));

            if (!pushResult.Success)
                throw new InvalidOperationException($"Force push failed (exit {pushResult.ExitCode}).");

            await Log("History purge complete. All collaborators must re-clone.");
            await bus.EmitAsync(opId, "complete", new { status = "success" });
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Purge history failed for {RepoPath}", repo.RepoPath);
            await bus.EmitAsync(opId, "error", new { message = ex.Message });
        }
        finally
        {
            bus.Complete(opId);
        }
    }

    private static async Task RunDeleteFilesAsync(
        ProjectConfig project, int repoIndex, List<string> files,
        string opId, BuildEventBus bus, IProcessRunner runner)
    {
        var repo = project.GitRepos[repoIndex];
        async Task Log(string msg) => await bus.EmitAsync(opId, "log", new { message = msg });

        try
        {
            await Log($"Deleting {files.Count} file(s) from {repo.RepoPath}…");

            foreach (var file in files)
            {
                await Log($"Removing: {file}");
                var rmArgs = new[] { "-C", repo.RepoPath, "rm", "-f", file };
                var rmResult = await runner.RunAsync("git", rmArgs, null,
                    line => bus.EmitAsync(opId, "log", new { message = line }),
                    line => bus.EmitAsync(opId, "log", new { message = line }));

                if (!rmResult.Success)
                    await Log($"Warning: git rm exited {rmResult.ExitCode} for {file} — may already be removed.");
            }

            // Commit all removals in one commit
            var fileList = string.Join(", ", files.Select(f => Path.GetFileName(f)));
            var commitMsg = $"chore: remove {fileList}";
            await Log($"Committing: {commitMsg}");

            var commitArgs = new[] { "-C", repo.RepoPath, "commit", "-m", commitMsg };
            var commitResult = await runner.RunAsync("git", commitArgs, null,
                line => bus.EmitAsync(opId, "log", new { message = line }),
                line => bus.EmitAsync(opId, "log", new { message = line }));

            if (!commitResult.Success)
                throw new InvalidOperationException($"git commit failed (exit {commitResult.ExitCode}).");

            await Log($"Pushing to origin/{repo.DeployBranch}…");
            var pushResult = await runner.RunAsync("git",
                ["-C", repo.RepoPath, "push", "origin", repo.DeployBranch],
                null,
                line => bus.EmitAsync(opId, "log", new { message = line }),
                line => bus.EmitAsync(opId, "log", new { message = line }));

            if (!pushResult.Success)
                throw new InvalidOperationException($"Push failed (exit {pushResult.ExitCode}).");

            await Log("Files deleted and pushed.");
            await bus.EmitAsync(opId, "complete", new { status = "success" });
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Delete files failed for {RepoPath}", repo.RepoPath);
            await bus.EmitAsync(opId, "error", new { message = ex.Message });
        }
        finally
        {
            bus.Complete(opId);
        }
    }

    private record PurgeHistoryRequest(List<string> Patterns);
    private record DeleteFilesRequest(List<string> Files);
}
