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
    public string? OverrideYaml { get; private set; }
    public string? EnvContent { get; private set; }
    public bool EnvWriteFails { get; set; }
    public string[]? SyncFailureLines { get; set; }
    public string[]? BuildFailureLines { get; set; }
    public string[]? ExtraBuildLines { get; set; }
    public bool FailStateWrite { get; set; }
    /// <summary>Render each buildable service's build folder inside the repository (so build fingerprints can be computed).</summary>
    public bool BuildContextInsideRepo { get; set; }
    // the stack folder
    public bool NoComposeFile { get; set; }
    /// <summary>Images (not built by ShipRight) that are not on the server yet.</summary>
    public HashSet<string> ThirdPartyMissing { get; } = [];
    public bool PullFails { get; set; }
    public bool StackConfigFails { get; set; }
    public bool StackWriteBlocked { get; set; }
    public string? StackConfigJson { get; set; }
    public string StackVersion { get; set; } = "2.29.7";
    public Dictionary<string, string> StackFiles { get; } = [];
    public List<Dictionary<string, string>> StackWrites { get; } = [];
    private string _lastConfigJson = "{}";
    /// <summary>What git says the build folder's tree is, by (7-char sha, service).</summary>
    public Func<string, string, string> TreeOf { get; set; } = (_, _) => "tree1";
    /// <summary>The image id docker reports for an image reference.</summary>
    public Func<string, string> ImageIdOf { get; set; } = r => "sha256:" + r[..r.LastIndexOf(':')];
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

    private (int, string[])? HandleStack(string cmd)
    {
        var m = Regex.Match(cmd, @"^: 'stack:(\w+)'; ");
        if (!m.Success) return null;
        switch (m.Groups[1].Value)
        {
            case "detect":
                Events.Add("stack-detect");
                return NoComposeFile ? Ok() : Ok("docker-compose.yml");
            case "write":
            {
                Events.Add("stack-write");
                if (StackWriteBlocked) return Fail("[stack] compose.yaml already exists and is not managed by ShipRight, so it was left alone.");
                var files = new Dictionary<string, string>();
                foreach (Match f in Regex.Matches(cmd, @"w (\S+) \d+ '([A-Za-z0-9+/=]*)'"))
                    files[f.Groups[1].Value] = Encoding.UTF8.GetString(Convert.FromBase64String(f.Groups[2].Value));
                StackWrites.Add(files);
                foreach (var kv in files) StackFiles[kv.Key] = kv.Value;
                return Ok();
            }
            case "config":
                Events.Add("stack-config");
                return StackConfigFails
                    ? Fail("error while loading compose file: (root) Additional property include is not allowed")
                    : Ok((StackConfigJson ?? _lastConfigJson).Split('\n'));
            case "version":
                Events.Add("stack-version");
                return Ok(StackVersion);
            case "remove":
                Events.Add("stack-remove");
                return Ok();
        }
        return null;
    }

    private (int, string[]) Handle(string cmd)
    {
        var prep = HandlePrepare(cmd);
        if (prep is not null) return prep.Value;
        var stack = HandleStack(cmd);
        if (stack is not null) return stack.Value;
        if (cmd.StartsWith(": 'images:missing'; "))
        {
            Events.Add("images-missing-check");
            var refs = Regex.Match(cmd, @"for r in ([^;]+);").Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim('\''));
            return Ok(refs.Where(ThirdPartyMissing.Contains).Select(r => "missing:" + r).ToArray());
        }
        if (cmd.Contains(" pull '"))
        {
            var names = Regex.Matches(cmd[(cmd.IndexOf(" pull ", StringComparison.Ordinal) + 6)..], @"'([^']+)'").Select(x => x.Groups[1].Value).ToList();
            Events.Add("pull:" + string.Join(",", names));
            if (PullFails) return Fail("Error response from daemon: toomanyrequests: You have reached your pull rate limit.");
            foreach (var n in names) { var s = Stack.FirstOrDefault(x => x.Name == n); if (s is not null) ThirdPartyMissing.Remove(DefaultImage(s)); }
            return Ok(names.Select(n => $" Image {n} Pulled").ToArray());
        }
        if (cmd.Contains("echo \"svc|") || cmd.Contains("echo \"compose|"))
        {
            Events.Add("fingerprint-probe");
            var psha = Short(Regex.Match(cmd, @"quiet '([0-9a-f]+)[:^]").Groups[1].Value);
            var rows = Regex.Matches(cmd, @"echo ""svc\|([a-z0-9_-]+)\|").Select(m => $"svc|{m.Groups[1].Value}|{TreeOf(psha, m.Groups[1].Value)}|dockerfile1|0");
            return Ok(new[] { "compose|c1" }.Concat(rows).ToArray());
        }
        if (cmd.Contains("inspect -f '{{.Id}}'"))
        {
            Events.Add("image-id");
            var iref = Regex.Match(cmd, @"\{\{\.Id\}\}' '([^']+)'").Groups[1].Value;
            return Images.Contains(iref) ? Ok(ImageIdOf(iref)) : Fail();
        }
        if (cmd.Contains("deploy.env") && cmd.Contains("base64 -d"))
        {
            if (EnvWriteFails) return Fail("cannot write the variables file");
            var m = Regex.Match(cmd, @"echo '([A-Za-z0-9+/=]+)' \| base64 -d");
            EnvContent = Encoding.UTF8.GetString(Convert.FromBase64String(m.Groups[1].Value));
            Events.Add("env-written");
            return Ok();
        }
        if (cmd.Contains("compose.override.yml") && cmd.Contains("base64 -d"))
        {
            var m = Regex.Match(cmd, @"echo '([A-Za-z0-9+/=]+)' \| base64 -d");
            OverrideYaml = Encoding.UTF8.GetString(Convert.FromBase64String(m.Groups[1].Value));
            Events.Add("override-written");
            return Ok();
        }
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
            return SyncOk ? Ok() : Fail(SyncFailureLines ?? ["fatal: unable to access the repository"]);
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
            _lastConfigJson = ComposeJsonFor?.Invoke(Short(CheckedOut)) ?? RenderCompose(cmd);
            return Ok(_lastConfigJson.Split('\n'));
        }
        if (cmd.Contains("build --progress=plain"))
        {
            var names = Regex.Matches(cmd[(cmd.IndexOf("--progress=plain", StringComparison.Ordinal) + 16)..], @"'([^']+)'")
                .Select(x => x.Groups[1].Value).ToList();
            Events.Add($"build:{string.Join(",", names)}");
            if (!BuildOk) return Fail(BuildFailureLines ?? ["ERROR: failed to solve: process \"/bin/sh -c npm ci\" did not complete successfully"]);
            if (KeepImagesAfterBuild)
                foreach (var n in names)
                {
                    var svc = Stack.Single(s => s.Name == n);
                    var r = Tags(cmd).GetValueOrDefault(svc.Var ?? "");
                    if (r is not null) Images.Add(r);
                    else ThirdPartyMissing.Remove(DefaultImage(svc));
                }
            return Ok(new[] { "#1 building..." }.Concat(ExtraBuildLines ?? []).Append("#9 naming to image done").ToArray());
        }
        if (cmd.Contains(" down -t "))
        {
            Events.Add("down");
            Running.Clear();
            return Ok();
        }
        if (cmd.Contains("up -d --no-build --pull never") && !cmd.Contains("--no-deps"))
        {
            Events.Add("up-all");
            var tags = Tags(cmd);
            foreach (var s in Stack)
            {
                var image = s.Var is not null && tags.TryGetValue(s.Var, out var r) ? r : DefaultImage(s);
                Running[s.Name] = image;
                _probeCounts[$"{s.Name}@{(image.Contains(':') ? image[(image.LastIndexOf(':') + 1)..] : "")}"] = 0;
            }
            return Ok();
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
            var build = s.Build ? ", \"build\": {\"context\": \"" + (BuildContextInsideRepo ? "/home/deploy/shop/repo/" + s.Name : "/r") + "\"}" : "";
            return $"\"{s.Name}\": {{ \"image\": \"{image}\"{build}{deps} }}";
        })));
        sb.Append("} }");
        return sb.ToString();
    }

    public ReleaseState ReadState() => ReleaseStateJson.Deserialize(StateJson!);
}
