using Serilog;
using ShipRight.Modules.Projects;
using ShipRight.Shared.ProcessRunner;
using ShipRight.Shared.SshRunner;

namespace ShipRight.Modules.JitDeploy;

public sealed record BranchesRequest(string? Url);

/// <summary>
/// Endpoints that let the build-on-server card offer what is already known (git addresses, branch, env file, existing
/// stacks and the repository's real branches) instead of asking the user to type it. All of them only read. The server's
/// connection always comes from the stored project, never from the request.
/// </summary>
public static class JitHintRoutes
{
    public static void MapJitHintRoutes(this WebApplication app)
    {
        app.MapGet("/api/jit/{id}/hints", (string id, IProjectStore projects, IProcessRunner runner) =>
            HandleHintsAsync(id, projects, runner));
        app.MapGet("/api/jit/{id}/stacks", (string id, IProjectStore projects, IJitConfigStore configs, ISshRunner ssh) =>
            HandleStacksAsync(id, projects, configs, ssh));
        app.MapPost("/api/jit/{id}/branches", (string id, BranchesRequest req, IProjectStore projects, ISshRunner ssh) =>
            HandleBranchesAsync(id, req, projects, ssh));
    }

    private static object Err(string message) => ProjectRouter.Error(message, null);

    private const string NoServer = "Choose the server this project deploys to first (its address, user, SSH key and folder).";

    internal static async Task<IResult> HandleHintsAsync(string id, IProjectStore projects, IProcessRunner runner)
    {
        var project = await projects.GetByIdAsync(id);
        if (project is null) return Results.NotFound(Err($"Project '{id}' not found."));
        return Results.Ok(await new JitHintsService(runner).BuildAsync(project));
    }

    internal static async Task<IResult> HandleStacksAsync(string id, IProjectStore projects, IJitConfigStore configs, ISshRunner ssh)
    {
        var project = await projects.GetByIdAsync(id);
        if (project is null) return Results.NotFound(Err($"Project '{id}' not found."));
        var target = JitDeployMapper.TargetOf(project);
        if (target is null) return Results.BadRequest(Err(NoServer));

        JitDeployConfig? cfg = null;
        try { cfg = await configs.GetAsync(id); } catch (InvalidDataException) { /* the card reports a damaged settings file itself */ }
        var name = JitDeployMapper.ComposeProjectName(project, cfg ?? new JitDeployConfig());

        var (exit, lines, failed) = await RunAsync(ssh, target, JitStacks.ListCommand());
        if (failed || exit != 0)
            return Results.Ok(new
            {
                stacks = Array.Empty<object>(), projectName = name, match = "none", warnings = Array.Empty<string>(), suggestedName = (string?)null,
                error = "Could not list the stacks on the server. Docker may not be installed yet, or the connection failed. This is only a convenience: you can still type a name.",
            });

        var report = JitStacks.Evaluate(JitStacks.Parse(lines), name, target.ProjectDir);
        return Results.Ok(new
        {
            stacks = report.Stacks.Select(s => new { name = s.Name, status = s.Status, configFiles = s.ConfigFiles }).ToList(),
            projectName = report.ProjectName,
            match = report.Match.ToString().ToLowerInvariant(),
            warnings = report.Warnings,
            suggestedName = report.SuggestedName,
            error = (string?)null,
        });
    }

    internal static async Task<IResult> HandleBranchesAsync(string id, BranchesRequest req, IProjectStore projects, ISshRunner ssh)
    {
        var project = await projects.GetByIdAsync(id);
        if (project is null) return Results.NotFound(Err($"Project '{id}' not found."));

        string command;
        try { command = JitBranches.Command(req.Url ?? string.Empty); }
        catch (ArgumentException ex) when (ex.Message.Contains("password or token"))
        {
            return Results.BadRequest(Err("That address contains a password or token. Remove it and use a deploy key instead."));
        }
        catch (ArgumentException)
        {
            return Results.BadRequest(Err("That does not look like a usable git address. Use one like git@github.com:you/your-app.git."));
        }

        var target = JitDeployMapper.TargetOf(project);
        if (target is null) return Results.BadRequest(Err(NoServer));

        var (exit, lines, failed) = await RunAsync(ssh, target, command);
        if (failed)
            return Results.Ok(new { branches = Array.Empty<string>(), defaultBranch = (string?)null, error = "Lost contact with the server while reading the branches. Try again." });
        if (exit != 0)
        {
            var why = lines.Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("[branches]", StringComparison.Ordinal));
            return Results.Ok(new
            {
                branches = Array.Empty<string>(), defaultBranch = (string?)null,
                error = why is null ? "Could not read the branches from the server." : why["[branches]".Length..].Trim(),
            });
        }

        var list = JitBranches.Parse(lines);
        return Results.Ok(new { branches = list.Branches, defaultBranch = list.DefaultBranch, error = (string?)null });
    }

    private static async Task<(int Exit, List<string> Lines, bool Failed)> RunAsync(ISshRunner ssh, JitTarget t, string command)
    {
        var lines = new List<string>();
        try
        {
            Task Collect(string l) { lock (lines) lines.Add(l); return Task.CompletedTask; }
            var exit = await ssh.RunAsync(t.Host, t.Username, t.SshKeyPath, command, Collect, Collect);
            lock (lines) return (exit, lines.ToList(), false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warning(ex, "A read-only hint command failed on {Host}", t.Host);
            return (-1, [], true);
        }
    }
}
