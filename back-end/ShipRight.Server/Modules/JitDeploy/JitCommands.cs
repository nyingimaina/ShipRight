namespace ShipRight.Modules.JitDeploy;

/// <summary>Which compose project a command acts on. <paramref name="EnvFile"/> is an absolute path, or null.</summary>
public sealed record ComposeContext(JitTarget Target, string Project, string? EnvFile = null);

/// <summary>Compose variable that carries a service's image, e.g. <c>API_TAG</c> with image <c>shop/api</c>; exported as <c>API_TAG=shop/api:&lt;release&gt;</c>.</summary>
public sealed record TagBinding(string TagVariable, string ImageRef);

public sealed record ProbeResult(ContainerState State, HealthStatus Health, int RestartCount);

/// <summary>
/// Builds the shell commands a build-on-server deploy sends over SSH. Every dynamic value is checked against an
/// allow-list (<see cref="JitNames"/>) and then single-quoted. Nothing here prunes, forces, or removes anything
/// except exact, release-tagged images that the caller names. Nothing names the deployer.
/// </summary>
public static class JitCommands
{
    private const string ProbeFormat = "{{.State.Status}}|{{if .State.Health}}{{.State.Health.Status}}{{end}}|{{.RestartCount}}";

    private static string Q(string v) => ShellQuote.Single(v);

    // ── preflight ────────────────────────────────────────────────────────────

    public static string CheckDocker() =>
        "docker info >/dev/null 2>&1 || { echo '[preflight] Docker is not available: it may not be installed, not running, or this user may not be allowed to use it.'; exit 1; }; " +
        "docker compose version >/dev/null 2>&1 || { echo '[preflight] Docker Compose (the \"docker compose\" plugin) is not installed.'; exit 1; }";

    /// <summary>Reports free space and refuses to continue below the minimum. It never frees space itself.</summary>
    public static string CheckDisk(JitTarget t, int minFreeMb)
    {
        if (minFreeMb <= 0) throw new ArgumentOutOfRangeException(nameof(minFreeMb), "The minimum free space must be above zero.");
        var dir = Q(JitNames.Path(t.ProjectDir));
        return $"mkdir -p {dir} && avail=$(df -Pm {dir} | awk 'NR==2{{print $4}}'); " +
               "echo \"[preflight] Free disk space: ${avail} MB\"; " +
               $"if [ \"$avail\" -lt {minFreeMb} ]; then echo \"[preflight] Not enough free disk space (${{avail}} MB; at least {minFreeMb} MB is needed). Free some space, then deploy again.\"; exit 1; fi";
    }

    /// <summary>The failure message deliberately does not repeat the address, which may contain a token.</summary>
    public static string CheckGit(string repoUrl) =>
        $"GIT_TERMINAL_PROMPT=0 timeout 30 git ls-remote --heads -- {Q(JitNames.RepoUrl(repoUrl))} >/dev/null 2>&1 || " +
        "{ echo '[preflight] Cannot reach the git repository. Check the address, the network, and that this server has access (a deploy key or token).'; exit 1; }";

    public static string EnsureImagesExist(IEnumerable<string> imageRefs)
    {
        var checks = imageRefs.Select(r => JitNames.ImageRef(r)).Distinct(StringComparer.Ordinal)
            .Select(r => $"docker image inspect {Q(r)} >/dev/null 2>&1 || {{ echo \"[preflight] The image {r} is missing from this server, so a rollback would not be possible.\"; exit 1; }}")
            .ToList();
        return checks.Count == 0 ? "true" : string.Join("; ", checks);
    }

    // ── git ──────────────────────────────────────────────────────────────────

    public static string SyncRepo(JitTarget t, string repoUrl)
    {
        var proj = Q(JitNames.Path(t.ProjectDir));
        var repo = Q(t.RepoDir);
        var url = Q(JitNames.RepoUrl(repoUrl));
        return $"export GIT_TERMINAL_PROMPT=0; mkdir -p {proj} && " +
               $"if [ -d {Q(t.RepoDir + "/.git")} ]; then git -C {repo} remote set-url origin {url}; else git clone --quiet -- {url} {repo} || exit 1; fi && " +
               $"git -C {repo} fetch --prune --tags --quiet origin";
    }

    public static string ResolveBranch(JitTarget t, string branch) =>
        $"git -C {Q(Repo(t))} rev-parse --verify --quiet {Q($"refs/remotes/origin/{JitNames.Branch(branch)}^{{commit}}")}";

    public static string ResolveCommit(JitTarget t, string sha) =>
        $"git -C {Q(Repo(t))} rev-parse --verify --quiet {Q($"{JitNames.Sha(sha)}^{{commit}}")}";

    /// <summary>The checkout is disposable: local changes are discarded, but only ever inside the repo folder.</summary>
    public static string Checkout(JitTarget t, string sha) =>
        $"cd {Q(Repo(t))} && git reset --hard --quiet && git clean -fdx --quiet && git checkout --detach --quiet {Q(JitNames.Sha(sha))} && " +
        "if [ -f .gitmodules ]; then git submodule update --init --recursive --quiet; fi";

    // ── compose ──────────────────────────────────────────────────────────────

    public static string ComposeConfig(ComposeContext ctx, IReadOnlyList<TagBinding> bindings, string tag) =>
        Prefix(ctx, bindings, tag) + Compose(ctx, null, "config --format json");

    public static string Build(ComposeContext ctx, IReadOnlyList<string> services, IReadOnlyList<TagBinding> bindings, string tag, int? niceLevel = null)
    {
        if (services.Count == 0) throw new ArgumentException("There is nothing to build.", nameof(services));
        if (niceLevel is < 0 or > 19) throw new ArgumentOutOfRangeException(nameof(niceLevel), "The build priority must be between 0 and 19.");
        var names = string.Join(" ", services.Select(s => Q(JitNames.Service(s))));
        return Prefix(ctx, bindings, tag) + Compose(ctx, niceLevel, $"build --progress=plain {names}");
    }

    public static string StopService(ComposeContext ctx, string service, int graceSeconds, IReadOnlyList<TagBinding> bindings, string tag)
    {
        if (graceSeconds < 0) throw new ArgumentOutOfRangeException(nameof(graceSeconds), "The stop grace period cannot be negative.");
        return Prefix(ctx, bindings, tag) + Compose(ctx, null, $"stop -t {graceSeconds} {Q(JitNames.Service(service))}");
    }

    /// <summary>Starts one service from images that already exist: no build, no pull, no touching its dependencies.</summary>
    public static string StartService(ComposeContext ctx, string service, IReadOnlyList<TagBinding> bindings, string tag) =>
        Prefix(ctx, bindings, tag) + Compose(ctx, null, $"up -d --no-deps --no-build --pull never {Q(JitNames.Service(service))}");

    /// <summary>Prints <c>state|health|restarts</c> for each container of the service; see <see cref="ProbeParser"/>.</summary>
    public static string ProbeService(ComposeContext ctx, string service, IReadOnlyList<TagBinding> bindings, string tag) =>
        Prefix(ctx, bindings, tag) +
        $"for id in $({Compose(ctx, null, $"ps -a -q {Q(JitNames.Service(service))}")}); do docker inspect -f {Q(ProbeFormat)} \"$id\"; done";

    // ── images ───────────────────────────────────────────────────────────────

    public static string ListImages() => "docker image ls --format '{{.Repository}}:{{.Tag}}'";

    public static string ListContainerImages() => "docker ps -a --format '{{.Image}}'";

    /// <summary>Removes exact release-tagged images, never forced (an image a container uses simply stays) and never failing the deploy.</summary>
    public static string RemoveImages(IEnumerable<string> imageRefs)
    {
        var cmds = new List<string>();
        foreach (var r in imageRefs.Distinct(StringComparer.Ordinal))
        {
            JitNames.ImageRef(r);
            var colon = r.LastIndexOf(':');
            if (colon <= r.LastIndexOf('/') || !ReleaseTag.TryParse(r[(colon + 1)..], out _, out _))
                throw new ArgumentException($"'{r}' is not a release image, so it will not be removed.");
            cmds.Add($"docker image rm {Q(r)} >/dev/null 2>&1 || echo \"[cleanup] Could not remove {r} (it may be in use).\"");
        }
        return cmds.Count == 0 ? "true" : string.Join("; ", cmds) + "; true";
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string Repo(JitTarget t) { JitNames.Path(t.ProjectDir); return t.RepoDir; }

    private static string Prefix(ComposeContext ctx, IReadOnlyList<TagBinding> bindings, string tag)
    {
        if (!ReleaseTag.TryParse(tag, out _, out _)) throw new ArgumentException($"'{tag}' is not a release tag.", nameof(tag));
        var exports = bindings.Select(b =>
            $"{JitNames.TagVariable(b.TagVariable)}={Q($"{JitNames.ImageRef(b.ImageRef)}:{tag}")}").ToList();
        var export = exports.Count == 0 ? string.Empty : "export " + string.Join(" ", exports) + " && ";
        return $"cd {Q(Repo(ctx.Target))} && {export}";
    }

    private static string Compose(ComposeContext ctx, int? nice, string args)
    {
        var env = ctx.EnvFile is null ? string.Empty : $" --env-file {Q(JitNames.Path(ctx.EnvFile))}";
        var prefix = nice is null ? string.Empty : $"nice -n {nice} ";
        return $"{prefix}docker compose -p {Q(JitNames.Project(ctx.Project))}{env} {args}";
    }
}

/// <summary>Reads the lines printed by <see cref="JitCommands.ProbeService"/>: one per container.</summary>
public static class ProbeParser
{
    public static IReadOnlyList<ProbeResult> Parse(string output)
    {
        var results = new List<ProbeResult>();
        foreach (var raw in (output ?? string.Empty).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var parts = line.Split('|');
            if (parts.Length != 3 || !int.TryParse(parts[2], out var restarts))
                throw new FormatException($"Unexpected container report: '{line}'.");
            try { results.Add(new ProbeResult(HealthGate.ParseState(parts[0]), HealthGate.ParseHealth(parts[1]), restarts)); }
            catch (ArgumentException ex) { throw new FormatException($"Unexpected container report: '{line}'. {ex.Message}"); }
        }
        return results;
    }
}

/// <summary>Reads <c>docker image ls</c> output and groups the images that belong to releases.</summary>
public static class ImageListParser
{
    public static IReadOnlyList<string> Parse(IEnumerable<string> lines) =>
        lines.Select(l => l.Trim())
             .Where(l => l.Length > 0 && !l.EndsWith(":<none>", StringComparison.Ordinal) && !l.StartsWith("<none>:", StringComparison.Ordinal))
             .ToList();

    /// <summary>Images of the given repositories whose tag is a release tag, grouped by release, oldest first.</summary>
    public static IReadOnlyList<ReleaseInfo> ReleasesFrom(IEnumerable<string> imageRefs, IReadOnlyCollection<string> repositories)
    {
        var byTag = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var r in imageRefs)
        {
            var colon = r.LastIndexOf(':');
            if (colon <= r.LastIndexOf('/')) continue;
            var repo = r[..colon];
            var tag = r[(colon + 1)..];
            if (!repositories.Contains(repo) || !ReleaseTag.TryParse(tag, out _, out _)) continue;
            if (!byTag.TryGetValue(tag, out var list)) byTag[tag] = list = [];
            if (!list.Contains(r)) list.Add(r);
        }
        return byTag
            .OrderBy(kv => { ReleaseTag.TryParse(kv.Key, out var c, out _); return c; })
            .Select(kv => new ReleaseInfo(kv.Key, kv.Value))
            .ToList();
    }
}
