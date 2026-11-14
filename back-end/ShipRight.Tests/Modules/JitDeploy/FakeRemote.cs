using System.Text;
using System.Text.RegularExpressions;
using ShipRight.Modules.JitDeploy;
using ShipRight.Shared.SshRunner;

namespace ShipRight.Tests.Modules.JitDeploy;

public sealed class FakeClock : IJitClock
{
    public DateTime UtcNow { get; private set; } = new(2026, 11, 13, 10, 0, 0, DateTimeKind.Utc);
    public int Delays { get; private set; }

    public Task DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Delays++;
        UtcNow += delay;
        return Task.CompletedTask;
    }
}

/// <summary>
/// A pretend server. It reads the real commands the deployer sends and behaves like docker, git and the shell would:
/// it keeps the state file, the images, the running containers and a log of what happened, and it can be told to fail
/// or to drop the connection at chosen moments.
/// </summary>
public sealed class FakeRemote : ISshRunner
{
    public const string Sha1 = "aaaaaaa1111111111111111111111111111111111";
    public const string Sha2 = "bbbbbbb2222222222222222222222222222222222";
    public const string Sha3 = "ccccccc3333333333333333333333333333333333";

    public sealed record Svc(string Name, string[] Deps, bool Build, string Image, string? Var);

    public List<Svc> Stack { get; set; } =
    [
        new("db", [], false, "postgres:16", null),
        new("api", ["db"], true, "shop/api", "API_TAG"),
        new("web", ["api"], true, "shop/web", "WEB_TAG"),
    ];

    // knobs
    public bool DockerOk { get; set; } = true;
    public int FreeMb { get; set; } = 50000;
    public bool GitOk { get; set; } = true;
    public bool SyncOk { get; set; } = true;
    public bool BuildOk { get; set; } = true;
    public bool KeepImagesAfterBuild { get; set; } = true;
    public bool FailStateWrite { get; set; }
    public bool ConfigFails { get; set; }
    public Dictionary<string, string> Branches { get; } = new() { ["master"] = Sha1 };
    public Func<string, string>? ComposeJsonFor { get; set; }               // by 7-char checked-out sha
    public Func<string, bool> StopFails { get; set; } = _ => false;
    public Func<string, string, bool> StartFails { get; set; } = (_, _) => false;   // (service, tag)
    public Func<string, string, int, string> Probe { get; set; } = (_, _, _) => "running|healthy|0";
    public Func<string, bool>? ThrowWhen { get; set; }                       // simulate a dropped connection
    public Action<string>? BeforeCommand { get; set; }

    // world
    public string? StateJson { get; set; }
    public HashSet<string> Images { get; } = [];
    public Dictionary<string, string> Running { get; } = [];                 // service -> image ref
    public string CheckedOut { get; private set; } = "";
    public List<string> Events { get; } = [];
    public List<string> Commands { get; } = [];
    public List<DeployPhase> Writes { get; } = [];

    private readonly Dictionary<string, int> _probeCounts = [];

    public async Task<int> RunAsync(string host, string username, string keyPath, string command,
        Func<string, Task>? onOutput = null, Func<string, Task>? onStderr = null, CancellationToken ct = default)
    {
        Commands.Add(command);
        BeforeCommand?.Invoke(command);
        ct.ThrowIfCancellationRequested();
        if (ThrowWhen?.Invoke(command) == true) throw new IOException("connection lost");

        var (exit, lines) = Handle(command);
        if (onOutput is not null) foreach (var l in lines) await onOutput(l);
        return exit;
    }

    private static string Short(string sha) => sha[..7];

    private (int, string[]) Ok(params string[] lines) => (0, lines);
    private (int, string[]) Fail(params string[] lines) => (1, lines);

    // ── server preparation (all tools present by default, so existing tests are unaffected) ──
    public HashSet<ServerTool> MissingTools { get; } = [];
    public Func<ServerTool, bool> InstallFails { get; set; } = _ => false;
    public bool InstallLiesAboutSuccess { get; set; }

    private (int, string[])? HandlePrepare(string cmd)
    {
        var m = Regex.Match(cmd, @"^: 'prepare:(\w+)';");
        if (!m.Success) return null;
        if (m.Groups[1].Value == "detect")
        {
            Events.Add("prepare-detect");
            var shown = MissingTools.Contains(ServerTool.Docker)
                ? MissingTools.Where(t => t is ServerTool.Git or ServerTool.Docker)
                : MissingTools;
            return Ok(shown.Select(t => "missing:" + t.ToString().ToLowerInvariant()).ToArray());
        }
        var tool = Enum.Parse<ServerTool>(m.Groups[1].Value, true);
        Events.Add("prepare:" + tool.ToString().ToLowerInvariant());
        if (InstallFails(tool)) return Fail("E: Unable to locate package " + tool);
        if (!InstallLiesAboutSuccess) MissingTools.Remove(tool);
        return Ok("installed " + tool);
    }

    private (int, string[]) Handle(string cmd)
    {
        var prep = HandlePrepare(cmd);
        if (prep is not null) return prep.Value;
        // ── state file ──
        if (cmd.Contains("base64 -d"))
        {
            if (FailStateWrite) return Fail();
            var m = Regex.Match(cmd, @"echo '([A-Za-z0-9+/=]+)' \| base64 -d");
            StateJson = Encoding.UTF8.GetString(Convert.FromBase64String(m.Groups[1].Value));
            Writes.Add(ReleaseStateJson.Deserialize(StateJson).Phase);
            return Ok();
        }
        if (cmd.Contains(ReleaseStateStore.NoStateSentinel))
            return StateJson is null ? Ok(ReleaseStateStore.NoStateSentinel) : Ok(StateJson.Split('\n'));

        // ── preflight ──
        if (cmd.StartsWith("docker info"))
        {
            Events.Add("docker-check");
            return DockerOk ? Ok() : Fail("[preflight] Docker is not available: it may not be installed.");
        }
        if (cmd.Contains("df -Pm"))
        {
            Events.Add("disk-check");
            var need = int.Parse(Regex.Match(cmd, @"-lt (\d+)").Groups[1].Value);
            return FreeMb >= need
                ? Ok($"[preflight] Free disk space: {FreeMb} MB")
                : Fail($"[preflight] Free disk space: {FreeMb} MB", $"[preflight] Not enough free disk space ({FreeMb} MB; at least {need} MB is needed).");
        }
        if (cmd.Contains("git ls-remote"))
        {
            Events.Add("git-check");
            return GitOk ? Ok() : Fail("[preflight] Cannot reach the git repository.");
        }
        if (cmd.Contains("docker image inspect"))
        {
            Events.Add("images-exist-check");
            var refs = Regex.Matches(cmd, @"docker image inspect '([^']+)'").Select(x => x.Groups[1].Value).ToList();
            var missing = refs.FirstOrDefault(r => !Images.Contains(r));
            return missing is null ? Ok() : Fail($"[preflight] The image {missing} is missing from this server, so a rollback would not be possible.");
        }

        // ── git ──
        if (cmd.Contains("git clone") || cmd.Contains("fetch --prune"))
        {
            Events.Add("sync");
            return SyncOk ? Ok() : Fail("fatal: unable to access the repository");
        }
        if (cmd.Contains("refs/remotes/origin/"))
        {
            var branch = Regex.Match(cmd, @"refs/remotes/origin/([^^']+)\^").Groups[1].Value;
            Events.Add($"resolve:{branch}");
            return Branches.TryGetValue(branch, out var sha) ? Ok(sha) : (1, []);
        }
        if (cmd.Contains("rev-parse --verify --quiet") && cmd.Contains("^{commit}"))
        {
            var sha = Regex.Match(cmd, @"quiet '([0-9a-f]+)\^").Groups[1].Value;
            Events.Add($"resolve-commit:{sha}");
            return Ok(sha.PadRight(40, '0'));
        }
        if (cmd.Contains("checkout --detach"))
        {
            CheckedOut = Regex.Match(cmd, @"--detach --quiet '([0-9a-f]+)'").Groups[1].Value;
            Events.Add($"checkout:{Short(CheckedOut)}");
            return Ok();
        }

        // ── compose ──
        if (cmd.Contains("config --format json"))
        {
            Events.Add("config");
            if (ConfigFails) return Fail("fatal: cannot change to the repository folder: No such file or directory");
            return Ok((ComposeJsonFor?.Invoke(Short(CheckedOut)) ?? RenderCompose(cmd)).Split('\n'));
        }
        if (cmd.Contains("build --progress=plain"))
        {
            var names = Regex.Matches(cmd[(cmd.IndexOf("--progress=plain", StringComparison.Ordinal) + 16)..], @"'([^']+)'")
                .Select(x => x.Groups[1].Value).ToList();
            Events.Add($"build:{string.Join(",", names)}");
            if (!BuildOk) return Fail("ERROR: failed to solve: process \"/bin/sh -c npm ci\" did not complete successfully");
            if (KeepImagesAfterBuild)
                foreach (var n in names)
                {
                    var svc = Stack.Single(s => s.Name == n);
                    var r = Tags(cmd).GetValueOrDefault(svc.Var ?? "");
                    if (r is not null) Images.Add(r);
                }
            return Ok("#1 building...", "#9 naming to image done");
        }
        if (cmd.Contains(" stop -t "))
        {
            var svc = Regex.Match(cmd, @"stop -t \d+ '([^']+)'").Groups[1].Value;
            Events.Add($"stop:{svc}");
            if (StopFails(svc)) return Fail($"error stopping {svc}");
            Running.Remove(svc);
            return Ok();
        }
        if (cmd.Contains("up -d --no-deps"))
        {
            var svc = Regex.Match(cmd, @"never '([^']+)'").Groups[1].Value;
            var def = Stack.Single(s => s.Name == svc);
            var image = def.Var is not null && Tags(cmd).TryGetValue(def.Var, out var r) ? r : DefaultImage(def);
            var tag = image.Contains(':') ? image[(image.LastIndexOf(':') + 1)..] : "";
            Events.Add($"start:{svc}@{tag}");
            if (StartFails(svc, tag)) return Fail($"error starting {svc}");
            Running[svc] = image;
            _probeCounts[$"{svc}@{tag}"] = 0;
            return Ok();
        }
        if (cmd.Contains("docker inspect -f"))
        {
            var svc = Regex.Match(cmd, @"ps -a -q '([^']+)'").Groups[1].Value;
            if (!Running.TryGetValue(svc, out var image)) { Events.Add($"probe:{svc}:none"); return Ok(); }
            var tag = image[(image.LastIndexOf(':') + 1)..];
            var n = _probeCounts[$"{svc}@{tag}"]++;
            Events.Add($"probe:{svc}");
            return Ok(Probe(svc, tag, n));
        }

        // ── images ──
        if (cmd.StartsWith("docker image ls")) return Ok(Images.ToArray());
        if (cmd.StartsWith("docker ps -a")) return Ok(Running.Values.ToArray());
        if (cmd.Contains("docker image rm"))
        {
            foreach (Match m in Regex.Matches(cmd, @"docker image rm '([^']+)'"))
            {
                var r = m.Groups[1].Value;
                if (Running.ContainsValue(r)) continue;              // docker refuses to remove an image in use
                if (Images.Remove(r)) Events.Add($"rm:{r}");
            }
            return Ok();
        }

        throw new InvalidOperationException("FakeRemote does not understand: " + cmd);
    }

    private static string DefaultImage(Svc s) => s.Var is null ? s.Image : s.Image + ":latest";

    private static Dictionary<string, string> Tags(string cmd) =>
        Regex.Matches(cmd, @"\b([A-Z][A-Z0-9_]*)='([^']+)'").ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);

    private string RenderCompose(string cmd)
    {
        var tags = Tags(cmd);
        var sb = new StringBuilder("{ \"name\": \"shop\", \"services\": {");
        sb.Append(string.Join(",", Stack.Select(s =>
        {
            var image = s.Var is not null && tags.TryGetValue(s.Var, out var r) ? r : DefaultImage(s);
            var deps = s.Deps.Length == 0 ? "" : ", \"depends_on\": {" + string.Join(",", s.Deps.Select(d => $"\"{d}\": {{\"condition\": \"service_started\"}}")) + "}";
            var build = s.Build ? ", \"build\": {\"context\": \"/r\"}" : "";
            return $"\"{s.Name}\": {{ \"image\": \"{image}\"{build}{deps} }}";
        })));
        sb.Append("} }");
        return sb.ToString();
    }

    public ReleaseState ReadState() => ReleaseStateJson.Deserialize(StateJson!);
}
