using System.Text.RegularExpressions;
using ShipRight.Shared.SshRunner;
using Serilog;
using ShipRight.Modules.Projects;

namespace ShipRight.Modules.JitDeploy;

public sealed record GitHostPage(string Label, string? Url);

/// <summary>
/// A read-only SSH key made on the server itself, so the server can read a private repository without any secret ever
/// leaving that machine. The key is an ordinary ed25519 key in the user's own ~/.ssh, named after the stack, and git is told to
/// use it only for the commands that reach the git host. Nothing here names the deployer.
/// </summary>
public static class JitDeployKey
{
    private static readonly Regex PublicKey = new(@"^ssh-ed25519 [A-Za-z0-9+/=]+( \S+)?$", RegexOptions.Compiled);
    private static readonly Regex NameShape = new(@"^deploy_[a-z0-9][a-z0-9_-]*$", RegexOptions.Compiled);

    public static string KeyName(string stack) => "deploy_" + JitNames.Project(stack);

    /// <summary>Makes the key only when it is missing (never replaces one), then prints its public half.</summary>
    public static string EnsureCommand(string stack)
    {
        var name = KeyName(stack);
        var file = $"~/.ssh/{name}";
        return "mkdir -p ~/.ssh && chmod 700 ~/.ssh; " +
               $"if [ ! -f {file} ]; then " +
               $"ssh-keygen -t ed25519 -N '' -C \"{name}@$(hostname 2>/dev/null || echo host)\" -f {file} >/dev/null 2>&1 || " +
               "{ echo '[deploykey] Could not make a key on the server. Check that ssh-keygen is installed.'; exit 1; }; " +
               "echo '[deploykey] created'; else echo '[deploykey] already there'; fi; " +
               $"cat {file}.pub";
    }

    public static string? ParsePublicKey(IEnumerable<string> lines) =>
        lines.Select(l => l.Trim()).FirstOrDefault(l => PublicKey.IsMatch(l));

    /// <summary>Makes git use the key for the commands that follow in the same shell line; never asks for a password.</summary>
    public static string GitSshPrefix(string keyName)
    {
        if (!NameShape.IsMatch(keyName)) throw new ArgumentException("The deploy key name is not valid.");
        return $"export GIT_SSH_COMMAND='ssh -i \"$HOME/.ssh/{keyName}\" -o IdentitiesOnly=yes -o BatchMode=yes -o StrictHostKeyChecking=accept-new'; ";
    }

    /// <summary>Where on the git host a deploy key is added, for the common hosts; others get a label only.</summary>
    public static GitHostPage HostPage(string? repoUrl)
    {
        var https = GitAddressForms.Analyse(repoUrl).Https;
        var m = https is null ? null : Regex.Match(https, @"^https://(?<host>[^/]+)/(?<path>.+?)(\.git)?$");
        if (m is null || !m.Success) return new GitHostPage("", null);
        var host = m.Groups["host"].Value;
        var path = m.Groups["path"].Value;
        return host.ToLowerInvariant() switch
        {
            "github.com" => new("GitHub", $"https://github.com/{path}/settings/keys"),
            "gitlab.com" => new("GitLab", $"https://gitlab.com/{path}/-/settings/repository"),
            "bitbucket.org" => new("Bitbucket", $"https://bitbucket.org/{path}/admin/access-keys/"),
            _ => new(host, null),
        };
    }
}

public sealed record DeployKeyRequest(string? RepoUrl);

public sealed record GitAccessRequest(string? RepoUrl, bool UseDeployKey);

/// <summary>
/// Endpoints behind the "give the server access to your code" panel: check that the server can read the repository, and make
/// (or fetch) a deploy key on the server to paste into the git host. The server's connection always comes from the stored
/// project. An address carrying a password or token is refused and never repeated.
/// </summary>
public static class JitDeployKeyRoutes
{
    public static void MapJitDeployKeyRoutes(this WebApplication app)
    {
        app.MapPost("/api/jit/{id}/deploy-key", (string id, DeployKeyRequest req, IProjectStore projects, IJitConfigStore configs, ISshRunner ssh) =>
            HandleCreateKeyAsync(id, req, projects, configs, ssh));
        app.MapPost("/api/jit/{id}/git-access", (string id, GitAccessRequest req, IProjectStore projects, IJitConfigStore configs, ISshRunner ssh) =>
            HandleGitAccessAsync(id, req, projects, configs, ssh));
    }

    private static object Err(string message) => ProjectRouter.Error(message, null);

    private const string NoServer = "Choose the server this project deploys to first (its address, user, SSH key and folder).";

    private static async Task<(JitDeployConfig? Config, string Stack)> StackOf(ProjectConfig project, string id, IJitConfigStore configs)
    {
        JitDeployConfig? cfg = null;
        try { cfg = await configs.GetAsync(id); } catch (InvalidDataException) { /* the card reports a damaged settings file itself */ }
        return (cfg, JitDeployMapper.ComposeProjectName(project, cfg ?? new JitDeployConfig()));
    }

    internal static async Task<IResult> HandleCreateKeyAsync(string id, DeployKeyRequest req, IProjectStore projects, IJitConfigStore configs, ISshRunner ssh)
    {
        var project = await projects.GetByIdAsync(id);
        if (project is null) return Results.NotFound(Err($"Project '{id}' not found."));
        var target = JitDeployMapper.TargetOf(project);
        if (target is null) return Results.BadRequest(Err(NoServer));

        var (cfg, stack) = await StackOf(project, id, configs);
        var keyName = JitDeployKey.KeyName(stack);
        var (exit, lines, failed) = await RunAsync(ssh, target, JitDeployKey.EnsureCommand(stack));
        var page = JitDeployKey.HostPage(string.IsNullOrWhiteSpace(req.RepoUrl) ? cfg?.RepoUrl : req.RepoUrl);

        if (failed) return Failure("Lost contact with the server while making the key. Try again.");
        if (exit != 0)
        {
            var why = lines.Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("[deploykey]", StringComparison.Ordinal));
            return Failure(why is null ? "Could not make a key on the server." : why["[deploykey]".Length..].Trim());
        }
        var key = JitDeployKey.ParsePublicKey(lines);
        if (key is null) return Failure("The server did not return a key. Try again.");

        Log.Information("Deploy key ready on the server for project {ProjectId}", id);
        return Results.Ok(new
        {
            publicKey = key,
            created = lines.Any(l => l.Contains("[deploykey] created")),
            keyName,
            hostLabel = page.Label,
            pageUrl = page.Url,
            error = (string?)null,
        });

        static IResult Failure(string message) => Results.Ok(new { publicKey = (string?)null, created = false, keyName = (string?)null, hostLabel = "", pageUrl = (string?)null, error = message });
    }

    internal static async Task<IResult> HandleGitAccessAsync(string id, GitAccessRequest req, IProjectStore projects, IJitConfigStore configs, ISshRunner ssh)
    {
        var project = await projects.GetByIdAsync(id);
        if (project is null) return Results.NotFound(Err($"Project '{id}' not found."));
        if (string.IsNullOrWhiteSpace(req.RepoUrl)) return Results.BadRequest(Err("Enter the git address first."));

        string command;
        try { command = JitCommands.CheckGit(req.RepoUrl.Trim()); }
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
        if (req.UseDeployKey)
        {
            var (_, stack) = await StackOf(project, id, configs);
            command = JitDeployKey.GitSshPrefix(JitDeployKey.KeyName(stack)) + command;
        }

        var (exit, lines, failed) = await RunAsync(ssh, target, command);
        if (failed) return Results.Ok(new { ok = false, usesDeployKey = req.UseDeployKey, message = "Lost contact with the server while checking. Try again." });
        if (exit == 0) return Results.Ok(new { ok = true, usesDeployKey = req.UseDeployKey, message = "The server can read your repository." });

        var why = lines.Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("[preflight]", StringComparison.Ordinal));
        return Results.Ok(new
        {
            ok = false, usesDeployKey = req.UseDeployKey,
            message = why is null ? "The server cannot read your repository yet." : why["[preflight]".Length..].Trim(),
        });
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
            Log.Warning(ex, "A deploy-key command failed on {Host}", t.Host);
            return (-1, [], true);
        }
    }
}
