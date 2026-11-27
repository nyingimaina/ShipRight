using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ShipRight.Modules.Projects;

namespace ShipRight.Modules.JitDeploy;

/// <summary>One command a real deploy would run, kept as a plan line for a dry run and the diagnostics file.</summary>
public sealed record JitPlanCommand(string Step, string Command);

/// <summary>Everything one deploy attempt should tell a person after the fact.</summary>
public sealed record JitAttemptContent(
    string Id,
    DateTime StartedUtc,
    string ProjectId,
    string ProjectName,
    string? Initiator,
    bool DryRun,
    string? Server,
    string? RepoUrl,
    string? Branch,
    string? Commit,
    string? BuildId,
    JitGuardReport? Guard,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Problems,
    IReadOnlyList<JitPlanCommand> Commands,
    string? Outcome,
    string? Message,
    string? Notes,
    string? BuildLog)
{
    /// <summary>The compose services a deploy would build.</summary>
    public IReadOnlyList<string> Services { get; init; } = [];
    /// <summary>The release tag the dry run expects. A live deploy only knows it after it resolves the commit.</summary>
    public string? Tag { get; init; }
}

/// <summary>
/// Renders a deploy attempt as ordinary text, so it can be read without ShipRight and attached to a bug report.
/// Nothing secret is written: addresses are redacted by <see cref="GitAddressForms.Redact"/> and the live deploy's
/// access token is passed in explicitly and replaced wherever it appears.
/// </summary>
public static class JitAttemptText
{
    public static string Render(JitAttemptContent c, IReadOnlyCollection<string> secrets)
    {
        var sb = new StringBuilder();
        sb.AppendLine("ShipRight build-on-server diagnostics");
        sb.AppendLine("=====================================");
        Row(sb, "Attempt", c.Id);
        Row(sb, "Started", c.StartedUtc.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture));
        Row(sb, "Mode", c.DryRun ? "Dry run (nothing on the server is changed)" : "Live deploy");
        Row(sb, "Project", $"{c.ProjectName} ({c.ProjectId})");
        if (!string.IsNullOrWhiteSpace(c.Initiator)) Row(sb, "Started by", c.Initiator);
        if (!string.IsNullOrWhiteSpace(c.Server)) Row(sb, "Server", c.Server);
        if (!string.IsNullOrWhiteSpace(c.RepoUrl)) Row(sb, "Repository", c.RepoUrl);
        if (!string.IsNullOrWhiteSpace(c.Branch)) Row(sb, "Deploy branch", c.Branch);
        Row(sb, "Commit", c.Commit ?? "(the branch tip, resolved on the server at deploy time)");
        if (!string.IsNullOrWhiteSpace(c.Tag)) Row(sb, "Release tag", c.Tag);
        if (c.Services.Count > 0) Row(sb, "Services", string.Join(", ", c.Services));
        if (!string.IsNullOrWhiteSpace(c.BuildId)) Row(sb, "Build id", c.BuildId);
        if (!string.IsNullOrWhiteSpace(c.Outcome)) Row(sb, "Outcome", c.Outcome);
        if (!string.IsNullOrWhiteSpace(c.Message)) Row(sb, "Message", c.Message);
        if (!string.IsNullOrWhiteSpace(c.Notes)) Row(sb, "Notes", c.Notes);
        sb.AppendLine();

        Section(sb, "Local checkout guard");
        if (c.Guard is null)
        {
            sb.AppendLine("Not run for this attempt (an explicit commit was given, or this operation does not build from a branch).");
        }
        else if (!c.Guard.Applicable)
        {
            sb.AppendLine("No local checkout matched this deploy's git address, so nothing local was checked.");
            sb.AppendLine("That is normal when the address exists only on the server or in CI.");
        }
        else
        {
            sb.AppendLine(c.Guard.Problem is null
                ? "Result: allowed. The commit the server will build is the one that was pushed."
                : "Result: blocked. No server work was started.");
            Row(sb, "Deploy branch", c.Guard.Branch);
            if (!string.IsNullOrWhiteSpace(c.Guard.InspectedRepoPath)) Row(sb, "Inspected", c.Guard.InspectedRepoPath);
            sb.AppendLine($"Checked {c.Guard.Repos.Count} matching local checkout{(c.Guard.Repos.Count == 1 ? "" : "s")}.");
            for (var i = 0; i < c.Guard.Repos.Count; i++) RenderProbe(sb, i + 1, c.Guard.Repos[i]);
            if (!string.IsNullOrWhiteSpace(c.Guard.Problem)) Row(sb, "Problem", c.Guard.Problem);
        }
        sb.AppendLine();

        Section(sb, "Warnings");
        if (c.Warnings.Count == 0) sb.AppendLine("None.");
        else foreach (var w in c.Warnings) sb.AppendLine("- " + w);
        sb.AppendLine();

        Section(sb, "Problems");
        if (c.Problems.Count == 0) sb.AppendLine("None.");
        else foreach (var p in c.Problems) sb.AppendLine("- " + p);
        sb.AppendLine();

        Section(sb, "What a real deploy would do");
        if (c.Commands.Count == 0) sb.AppendLine("No plan was produced.");
        else
        {
            for (var i = 0; i < c.Commands.Count; i++)
            {
                sb.AppendLine($"{i + 1}. [{c.Commands[i].Step}]");
                sb.AppendLine("   " + c.Commands[i].Command.Replace("\n", "\n   ", StringComparison.Ordinal));
            }
        }
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(c.BuildLog))
        {
            Section(sb, "Deploy log");
            sb.AppendLine(c.BuildLog.TrimEnd());
            sb.AppendLine();
        }

        return GitAddressForms.Redact(sb.ToString(), secrets);
    }

    private static void RenderProbe(StringBuilder sb, int number, JitRepoProbe p)
    {
        sb.AppendLine();
        sb.AppendLine($"Checkout {number}: {p.Path}");
        Row(sb, "  origin", p.Origin);
        Row(sb, "  current branch", p.CurrentBranch ?? "(unknown)");
        Row(sb, "  is the deploy branch", p.OnDeployBranch ? "yes" : "no (the pushed branch was checked instead of what is on disk)");
        Row(sb, "  remote refreshed", p.RemoteRefreshed ? "yes" : "no (the last fetched copy was used)");
        Row(sb, "  tracked changes", p.TrackedChanges.Count == 0 ? "none" : string.Join(", ", p.TrackedChanges));
        Row(sb, "  untracked files", p.UntrackedFiles.Count == 0 ? "none" : string.Join(", ", p.UntrackedFiles));
        Row(sb, "  local branch ref", p.LocalRef ?? "(missing)");
        Row(sb, "  remote branch ref", p.RemoteRef ?? "(missing)");
        Row(sb, "  ahead / behind", $"{p.Ahead} / {p.Behind}");
        foreach (var w in p.Warnings) sb.AppendLine("  warning: " + w);
        if (!string.IsNullOrWhiteSpace(p.Problem)) sb.AppendLine("  problem: " + p.Problem);
    }

    private static void Section(StringBuilder sb, string title)
    {
        sb.AppendLine("── " + title + " " + new string('─', Math.Max(3, 58 - title.Length)));
    }

    private static void Row(StringBuilder sb, string label, string? value) =>
        sb.AppendLine($"{label}: {value}");
}

/// <summary>
/// Where deploy-attempt diagnostics live: one plain text file per attempt, in a folder per project. The project folder
/// means a download link can only ever reach that project's own attempts, and the id is checked so a crafted link can
/// never leave the diagnostics folder. Writes are atomic; retention keeps the newest <see cref="Retained"/> per project.
/// Diagnosing a deploy must never be able to fail it, so callers only ever get a best-effort write.
/// </summary>
public sealed class JitDeployAttemptStore
{
    public const int Retained = 100;

    private readonly string _root;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JitDeployAttemptStore(string dataDir) =>
        _root = Path.Combine(dataDir, "jit-deploy", "attempts");

    public static bool IsSafeId(string? id) =>
        !string.IsNullOrWhiteSpace(id) && id.Length <= 64 &&
        id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    public static string NewId(DateTime utcNow) => $"{utcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";

    public static string UrlFor(string projectId, string attemptId) =>
        $"/api/jit/{Uri.EscapeDataString(projectId)}/deploy-attempts/{attemptId}/download";

    public async Task WriteAsync(string projectId, string attemptId, string text, CancellationToken ct = default)
    {
        if (!IsSafeId(attemptId)) throw new ArgumentException("The attempt id is not valid.", nameof(attemptId));

        await _gate.WaitAsync(ct);
        try
        {
            var dir = ProjectDir(projectId);
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, attemptId + ".log");
            var tmp = Path.Combine(dir, attemptId + "." + Guid.NewGuid().ToString("N") + ".tmp");
            await File.WriteAllTextAsync(tmp, text, ct);
            File.Move(tmp, file, overwrite: true);
            Prune(dir);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string?> ReadAsync(string projectId, string attemptId, CancellationToken ct = default)
    {
        if (!IsSafeId(attemptId)) return null;
        var file = Path.Combine(ProjectDir(projectId), attemptId + ".log");
        if (!File.Exists(file)) return null;
        return await File.ReadAllTextAsync(file, ct);
    }

    private string ProjectDir(string projectId) => Path.Combine(_root, ProjectKey(projectId));

    /// <summary>A readable folder name plus a short hash, so two projects that sanitise to the same word still differ.</summary>
    internal static string ProjectKey(string projectId)
    {
        var id = projectId ?? string.Empty;
        var slug = new string(id.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        if (slug.Length == 0) slug = "project";
        if (slug.Length > 40) slug = slug[..40];
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)))[..8].ToLowerInvariant();
        return slug + "-" + hash;
    }

    private static void Prune(string dir)
    {
        try
        {
            var files = new DirectoryInfo(dir).GetFiles("*.log")
                .OrderByDescending(f => f.Name, StringComparer.Ordinal)
                .Skip(Retained)
                .ToList();
            foreach (var f in files)
                try { f.Delete(); } catch { /* best effort */ }
        }
        catch { /* best effort */ }
    }
}

/// <summary>
/// Collects one attempt while the deploy preflight runs, then writes it once at a time. It is deliberately mutable:
/// the caller adds a warning here and a plan line there without threading a record through every early return.
/// </summary>
public sealed class JitAttemptBuilder
{
    private readonly JitDeployAttemptStore? _store;
    private readonly List<string> _warnings = [];
    private readonly List<string> _problems = [];
    private readonly List<JitPlanCommand> _commands = [];
    private readonly List<string> _secrets = [];
    private IReadOnlyList<string> _services = [];
    private string? _tag;

    public JitAttemptBuilder(JitDeployAttemptStore? store, ProjectConfig project, JitDeployConfig? cfg,
        string? initiator, bool dryRun, DateTime utcNow)
    {
        _store = store;
        Id = JitDeployAttemptStore.NewId(utcNow);
        StartedUtc = utcNow;
        ProjectId = project.Id;
        ProjectName = project.Name;
        Initiator = initiator;
        DryRun = dryRun;
        Server = string.IsNullOrWhiteSpace(project.Server.Host)
            ? null
            : $"{project.Server.Username}@{project.Server.Host} ({project.Server.RemoteWorkingDir})";
        RepoUrl = string.IsNullOrWhiteSpace(cfg?.RepoUrl) ? null : GitAddressForms.Analyse(cfg.RepoUrl).Original;
        Branch = string.IsNullOrWhiteSpace(cfg?.Branch) ? null : cfg.Branch;
    }

    public string Id { get; }
    public DateTime StartedUtc { get; }
    public string ProjectId { get; }
    public string ProjectName { get; }
    public string? Initiator { get; }
    public bool DryRun { get; }
    public string? Server { get; }
    public string? RepoUrl { get; }
    public string? Branch { get; }
    public string? Commit { get; private set; }
    public string? BuildId { get; private set; }
    public JitGuardReport? Guard { get; private set; }
    public string? Outcome { get; private set; }
    public string? Message { get; private set; }
    public string? Notes { get; private set; }
    public string? BuildLog { get; private set; }
    public IReadOnlyList<string> Warnings => _warnings;
    public IReadOnlyList<string> Problems => _problems;
    public IReadOnlyList<JitPlanCommand> Commands => _commands;
    public IReadOnlyList<string> Secrets => _secrets;
    public string? Url => _store is null ? null : JitDeployAttemptStore.UrlFor(ProjectId, Id);

    public JitAttemptBuilder Warn(string warning) { _warnings.Add(warning); return this; }
    public JitAttemptBuilder Warn(IEnumerable<string> warnings) { _warnings.AddRange(warnings); return this; }
    public JitAttemptBuilder Problem(string problem) { _problems.Add(problem); return this; }
    public JitAttemptBuilder AddProblems(IEnumerable<string> problems) { _problems.AddRange(problems); return this; }
    public JitAttemptBuilder Plan(IEnumerable<JitPlanCommand> commands) { _commands.AddRange(commands); return this; }
    public JitAttemptBuilder WithGuard(JitGuardReport? guard) { Guard = guard; return this; }
    public JitAttemptBuilder WithServices(IEnumerable<string> services) { _services = services.ToList(); return this; }
    public JitAttemptBuilder WithTag(string? tag) { _tag = tag; return this; }
    public JitAttemptBuilder WithCommit(string? commit) { Commit = commit; return this; }
    public JitAttemptBuilder WithBuild(string? buildId) { BuildId = buildId; return this; }
    public JitAttemptBuilder WithOutcome(string outcome, string? message = null) { Outcome = outcome; Message = message; return this; }
    public JitAttemptBuilder WithNotes(string? notes) { Notes = notes; return this; }
    public JitAttemptBuilder WithBuildLog(string? log)
    {
        const int max = 200_000;
        BuildLog = log is null ? null
            : log.Length <= max ? log
            : "[… the start of the log is omitted here; the full log is in the build history …]\n" + log[^max..];
        return this;
    }
    public JitAttemptBuilder Secret(string? secret)
    {
        if (!string.IsNullOrEmpty(secret) && secret.Length >= 8) _secrets.Add(secret);
        return this;
    }

    public string Render() => JitAttemptText.Render(new JitAttemptContent(
        Id, StartedUtc, ProjectId, ProjectName, Initiator, DryRun, Server, RepoUrl, Branch, Commit, BuildId,
        Guard, _warnings, _problems, _commands, Outcome, Message, Notes, BuildLog)
    { Services = _services, Tag = _tag }, _secrets);

    /// <summary>Best effort: a diagnostics failure must never turn a deploy into a failure.</summary>
    public async Task SaveAsync(CancellationToken ct = default)
    {
        if (_store is null) return;
        try { await _store.WriteAsync(ProjectId, Id, Render(), ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Serilog.Log.Warning(ex, "Could not write the diagnostics for deploy attempt {AttemptId}", Id);
        }
    }
}
