using ShipRight.Modules.JitDeploy;
using ShipRight.Modules.Platform;
using ShipRight.Modules.Projects;

namespace ShipRight.Modules.BootstrapDeployment;

/// <summary>
/// The first real consumer of the rule engine: it checks a bootstrap plan's facts without touching the server. The
/// results inform the screen; they never stop the configuration from being written.
/// </summary>
public static class BootstrapRules
{
    private static readonly IReadOnlyList<RuleDefinition> Defaults =
    [
        new("server.ssh", "SSH access", "ShipRight can reach the server.", RuleScope.Project, RuleSeverity.Blocking, "server.ssh"),
        new("compose.file", "Compose file", "The stack is described by a compose file.", RuleScope.Project, RuleSeverity.Blocking, "compose.file"),
        new("tls.domain", "HTTPS", "Production sites serve HTTPS.", RuleScope.Project, RuleSeverity.Advisory, "tls.domain"),
        new("data.backups", "Backups", "A database has backups.", RuleScope.Project, RuleSeverity.Advisory, "data.backups"),
    ];

    public static IReadOnlyList<RuleResult> Evaluate(
        BootstrapRequest request, ProjectConfig project, JitDeployConfig jit, ServerConfig server)
    {
        var hasDatabase = jit.EnvText.Contains("MYSQL_DATABASE=", StringComparison.Ordinal)
                          || project.Database is not null;

        var context = new RuleContext(
            ProjectId: project.Id,
            Environment: "staging",
            HasDomain: !string.IsNullOrWhiteSpace(request.Domain),
            HasBackups: false,
            HasSshAccess: !string.IsNullOrWhiteSpace(server.Username),
            HasComposeFile: !string.IsNullOrWhiteSpace(jit.ComposeFile),
            HasDatabase: hasDatabase,
            Production: false);

        return new RuleEngine().Evaluate(context, Defaults).Results;
    }
}
