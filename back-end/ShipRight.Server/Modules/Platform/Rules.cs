namespace ShipRight.Modules.Platform;

/// <summary>How broad a rule is. Narrower scopes may tighten a broader rule, never weaken it.</summary>
public enum RuleScope
{
    Tenant = 0,
    Company = 1,
    Project = 2,
    Environment = 3,
}

/// <summary>Advisory informs; Blocking stops a deploy; Locked cannot be waived.</summary>
public enum RuleSeverity
{
    Advisory = 0,
    Blocking = 1,
    Locked = 2,
}

public enum RuleStatus
{
    Pass,
    Fail,
    Unknown,
    Waived,
    NotApplicable,
}

public sealed record RuleDefinition(
    string Id,
    string Title,
    string Description,
    RuleScope Scope,
    RuleSeverity Severity,
    string CheckId,
    string? Remediation = null);

/// <summary>A time-bound, named exception. Locked rules ignore waivers.</summary>
public sealed record RuleWaiver(
    string RuleId,
    string ProjectId,
    string? Environment,
    string Reason,
    string ApprovedBy,
    DateTimeOffset ExpiresAt);

/// <summary>The facts a rule can see. It deliberately contains no secrets and no target access.</summary>
public sealed record RuleContext(
    string? ProjectId = null,
    string? Environment = null,
    bool HasDomain = false,
    bool HasBackups = false,
    bool HasSshAccess = false,
    bool HasComposeFile = false,
    bool HasDatabase = false,
    bool Production = false);

public sealed record RuleResult(
    string RuleId,
    string Title,
    RuleScope Scope,
    RuleSeverity Severity,
    RuleStatus Status,
    string Message,
    string? Remediation = null);

public sealed record RuleEvaluation(IReadOnlyList<RuleResult> Results)
{
    /// <summary>Failures and unknowns block only when they are Blocking or Locked. Advisory results never block.</summary>
    public bool Blocked => Results.Any(result =>
        (result.Status is RuleStatus.Fail or RuleStatus.Unknown) &&
        (result.Severity is RuleSeverity.Blocking or RuleSeverity.Locked));

    public IReadOnlyList<RuleResult> Failures =>
        Results.Where(result => result.Status == RuleStatus.Fail).ToList();
}

public interface IRuleEngine
{
    RuleEvaluation Evaluate(
        RuleContext context,
        IReadOnlyList<RuleDefinition> definitions,
        IReadOnlyList<RuleWaiver>? waivers = null,
        DateTimeOffset? now = null);
}

/// <summary>Evaluates definitions against facts. It never touches a server, a file or a network.</summary>
public sealed class RuleEngine : IRuleEngine
{
    public RuleEvaluation Evaluate(
        RuleContext context,
        IReadOnlyList<RuleDefinition> definitions,
        IReadOnlyList<RuleWaiver>? waivers = null,
        DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        var results = new List<RuleResult>();

        foreach (var definition in RuleResolver.Resolve(definitions))
        {
            var (status, message) = RuleCatalog.Check(definition.CheckId, context);

            var waiver = status == RuleStatus.Fail && definition.Severity != RuleSeverity.Locked
                ? FindWaiver(waivers, definition.Id, context, at)
                : null;

            if (waiver is not null)
            {
                status = RuleStatus.Waived;
                message = $"Waived by {waiver.ApprovedBy} until {waiver.ExpiresAt:yyyy-MM-dd}: {waiver.Reason}";
            }

            results.Add(new RuleResult(
                definition.Id, definition.Title, definition.Scope, definition.Severity,
                status, message, definition.Remediation));
        }

        return new RuleEvaluation(results);
    }

    private static RuleWaiver? FindWaiver(
        IReadOnlyList<RuleWaiver>? waivers, string ruleId, RuleContext context, DateTimeOffset now) =>
        waivers?.FirstOrDefault(waiver =>
            string.Equals(waiver.RuleId, ruleId, StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrEmpty(waiver.ProjectId) ||
             string.Equals(waiver.ProjectId, context.ProjectId, StringComparison.Ordinal)) &&
            (string.IsNullOrEmpty(waiver.Environment) ||
             string.Equals(waiver.Environment, context.Environment, StringComparison.OrdinalIgnoreCase)) &&
            waiver.ExpiresAt > now);
}

/// <summary>
/// Collapses the same rule defined at several scopes. The narrowest definition supplies the text, while the strictest
/// severity wins: a narrower scope may add or tighten a rule, never weaken a locked one.
/// </summary>
public static class RuleResolver
{
    public static IReadOnlyList<RuleDefinition> Resolve(IReadOnlyList<RuleDefinition> definitions) =>
        definitions
            .GroupBy(definition => definition.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var severity = group.Max(definition => definition.Severity);
                var narrowest = group.OrderByDescending(definition => (int)definition.Scope).First();
                return narrowest with { Severity = severity };
            })
            .OrderBy(definition => definition.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
}

/// <summary>The checks a rule can name. Unknown checks fail closed for locked rules and inform for the rest.</summary>
public static class RuleCatalog
{
    public static (RuleStatus Status, string Message) Check(string checkId, RuleContext context) => checkId switch
    {
        "server.ssh" => context.HasSshAccess
            ? (RuleStatus.Pass, "The server can be reached over SSH.")
            : (RuleStatus.Fail, "Add the user and key so ShipRight can reach the server."),

        "compose.file" => context.HasComposeFile
            ? (RuleStatus.Pass, "A compose file describes the stack.")
            : (RuleStatus.Fail, "Choose the compose file inside the repository."),

        "data.backups" => !context.HasDatabase || context.HasBackups
            ? (RuleStatus.Pass, "Backups are covered.")
            : (RuleStatus.Fail, "Turn on backups for the database."),

        "tls.domain" => !context.Production || context.HasDomain
            ? (RuleStatus.Pass, "A domain is available for HTTPS.")
            : (RuleStatus.Fail, "Add a domain so the app can serve HTTPS."),

        _ => (RuleStatus.Unknown, "This check is not available yet."),
    };
}

/// <summary>Turns today's human warnings into advisory results, so old and new agree before anything blocks.</summary>
public static class LegacyRuleAdapter
{
    public static RuleEvaluation FromProblems(IReadOnlyList<string> problems) =>
        new(problems
            .Select((problem, index) => new RuleResult(
                $"legacy.{index}", "Legacy deployment check", RuleScope.Project,
                RuleSeverity.Advisory, RuleStatus.Fail, problem))
            .ToList());
}
