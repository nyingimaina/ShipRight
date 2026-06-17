using Serilog;
using ShipRight.Modules.Projects;
using ShipRight.Shared.Events;
using ShipRight.Shared.SshRunner;

namespace ShipRight.Modules.Ssh;

public static class SshTerminalRouter
{
    public static void MapSshTerminalRoutes(this WebApplication app)
    {
        app.MapPost("/api/projects/{id}/ssh/exec", async (
            string id, ExecRequest request,
            IProjectStore store, ISshRunner ssh, BuildEventBus bus) =>
        {
            var project = await store.GetByIdAsync(id);
            if (project is null)
                return Results.NotFound(new { isError = true, message = $"Project '{id}' not found." });

            var trimmed = request.Command?.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
                return Results.BadRequest(new { isError = true, message = "command is required." });

            if (string.IsNullOrWhiteSpace(project.Server.Host))
                return Results.BadRequest(new { isError = true, message = "Project has no server host configured." });

            var opId = Guid.NewGuid().ToString("N");
            bus.Register(opId);
            var sessionKey = $"project:{id}";
            var cwd = SshSessionStore.GetCwd(sessionKey);

            if (trimmed == "pwd")
            {
                _ = Task.Run(async () =>
                {
                    await bus.EmitAsync(opId, "cwd", new { cwd });
                    await bus.EmitAsync(opId, "log", new { source = "stdout", line = cwd });
                    await bus.EmitAsync(opId, "done", new { exitCode = 0, cwd });
                    bus.Complete(opId);
                });
                return Results.Accepted($"/api/projects/{id}/ssh/ops/{opId}/stream", new { opId });
            }

            var isCd = trimmed == "cd" || trimmed.StartsWith("cd ");
            string? actualCommand = null;
            string? newCwd = null;

            if (isCd)
            {
                var parts = trimmed.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                var target = parts.Length > 1 ? parts[1].Trim() : "~";

                if (target == "~" || target.StartsWith('/'))
                {
                    newCwd = target;
                }
                else
                {
                    actualCommand = $"cd {target} && pwd";
                }
            }
            else
            {
                actualCommand = $"cd {cwd} && {request.Command}";
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    if (isCd && newCwd != null)
                    {
                        SshSessionStore.SetCwd(sessionKey, newCwd);
                        await bus.EmitAsync(opId, "cwd", new { cwd = newCwd });
                        await bus.EmitAsync(opId, "done", new { exitCode = 0, cwd = newCwd });
                        return;
                    }

                    Log.Information("SSH terminal exec for {SessionKey}: {Command}", sessionKey, actualCommand);

                    if (isCd)
                    {
                        var captured = new List<string>();
                        var exit = await ssh.RunAsync(
                            project.Server.Host,
                            project.Server.Username,
                            project.Server.SshKeyPath,
                            actualCommand!,
                            onOutput: line => { captured.Add(line); return Task.CompletedTask; },
                            onStderr: line => bus.EmitAsync(opId, "log", new { source = "stderr", line }));

                        var resolved = captured.LastOrDefault(l => !string.IsNullOrWhiteSpace(l));
                        newCwd = !string.IsNullOrWhiteSpace(resolved) ? resolved : cwd;
                        SshSessionStore.SetCwd(sessionKey, newCwd);
                        await bus.EmitAsync(opId, "cwd", new { cwd = newCwd });
                        await bus.EmitAsync(opId, "done", new { exitCode = exit, cwd = newCwd });
                    }
                    else
                    {
                        var exit = await ssh.RunAsync(
                            project.Server.Host,
                            project.Server.Username,
                            project.Server.SshKeyPath,
                            actualCommand!,
                            onOutput: line => bus.EmitAsync(opId, "log", new { source = "stdout", line }),
                            onStderr: line => bus.EmitAsync(opId, "log", new { source = "stderr", line }));

                        await bus.EmitAsync(opId, "done", new { exitCode = exit, cwd = SshSessionStore.GetCwd(sessionKey) });
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "SSH terminal exec failed for {SessionKey}", sessionKey);
                    await bus.EmitAsync(opId, "error", new { message = ex.Message });
                }
                finally
                {
                    bus.Complete(opId);
                }
            });

            return Results.Accepted($"/api/projects/{id}/ssh/ops/{opId}/stream", new { opId });
        });

        app.MapGet("/api/projects/{id}/ssh/ops/{opId}/stream", async (
            string id, string opId,
            BuildEventBus eventBus, HttpContext http, CancellationToken ct) =>
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

            var reader = eventBus.Subscribe(opId);
            try
            {
                await foreach (var payload in reader.ReadAllAsync(cts.Token))
                {
                    await http.Response.WriteAsync($"data: {payload}\n\n", cts.Token);
                    await http.Response.Body.FlushAsync(cts.Token);
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                await cts.CancelAsync();
                await heartbeat;
                eventBus.Unsubscribe(opId, reader);
            }
        });
    }

    private record ExecRequest(string Command);
}
