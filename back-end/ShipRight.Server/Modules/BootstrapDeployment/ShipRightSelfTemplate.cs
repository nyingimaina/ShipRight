using System.Security.Cryptography;

namespace ShipRight.Modules.BootstrapDeployment;

/// <summary>
/// The "deploy this product onto a fresh box" preset. It supplies only what is knowable before a person says where:
/// the repository, the compose file, the one service ShipRight builds, and a complete environment. Secrets that were
/// not supplied are generated here, so the first deploy is not blocked on a hand-written .env.
/// </summary>
public static class ShipRightSelfTemplate
{
    public const string DefaultRepoUrl = "git@github.com:nyingimaina/ShipRight.git";
    public const string DefaultComposeFile = "docker-compose.yml";
    public const string DefaultComposeProject = "shipright";

    /// <summary>The self preset with the target filled in. Everything else can still be overridden.</summary>
    public static BootstrapRequest Create(
        string host,
        string username,
        string remoteWorkingDir,
        string domain,
        string? sshKeyPath = null,
        string? repoUrl = null,
        string? branch = null,
        IReadOnlyDictionary<string, string>? env = null) => new()
    {
        Preset = BootstrapPreset.ShipRightSelf,
        ProjectName = "ShipRight",
        RepoUrl = string.IsNullOrWhiteSpace(repoUrl) ? DefaultRepoUrl : repoUrl!,
        Branch = string.IsNullOrWhiteSpace(branch) ? "master" : branch!,
        ComposeFile = DefaultComposeFile,
        ComposeProject = DefaultComposeProject,
        Services =
        [
            new BootstrapService { Name = "app", Image = "shipright/app", Dockerfile = "Dockerfile", Context = "." },
        ],
        ServerName = "staging",
        Host = host,
        Username = username,
        SshKeyPath = sshKeyPath ?? string.Empty,
        RemoteWorkingDir = remoteWorkingDir,
        Domain = domain,
        HealthCheckUrl = string.IsNullOrWhiteSpace(domain)
            ? "http://localhost:5200/api/health"
            : $"https://{domain}/api/health",
        Env = env is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(env, StringComparer.Ordinal),
    };

    /// <summary>Fills only the blanks; values the caller supplied always win.</summary>
    public static BootstrapRequest FillDefaults(BootstrapRequest request)
    {
        if (request.Preset != BootstrapPreset.ShipRightSelf) return request;

        var domain = (request.Domain ?? string.Empty).Trim();
        var env = new Dictionary<string, string>(request.Env ?? new Dictionary<string, string>(), StringComparer.Ordinal);

        void Add(string key, string value)
        {
            if (!env.TryGetValue(key, out var existing) || string.IsNullOrEmpty(existing)) env[key] = value;
        }

        var filled = request;
        if (string.IsNullOrWhiteSpace(filled.ProjectName)) filled = filled with { ProjectName = "ShipRight" };
        if (string.IsNullOrWhiteSpace(filled.RepoUrl)) filled = filled with { RepoUrl = DefaultRepoUrl };
        if (string.IsNullOrWhiteSpace(filled.Branch)) filled = filled with { Branch = "master" };
        if (string.IsNullOrWhiteSpace(filled.ComposeFile)) filled = filled with { ComposeFile = DefaultComposeFile };
        if (string.IsNullOrWhiteSpace(filled.ComposeProject)) filled = filled with { ComposeProject = DefaultComposeProject };
        if (filled.Services.Count == 0)
            filled = filled with
            {
                Services =
                [
                    new BootstrapService { Name = "app", Image = "shipright/app", Dockerfile = "Dockerfile", Context = "." },
                ],
            };

        Add("SHIPRIGHT__MODE", "Cloud");
        Add("SHIPRIGHT__TIME_ZONE", "Africa/Nairobi");
        Add("MYSQL_DATABASE", "shipright");
        Add("MYSQL_USER", "shipright");
        Add("MYSQL_ROOT_PASSWORD", Secret());
        Add("MYSQL_PASSWORD", Secret());

        if (domain.Length > 0)
        {
            Add("SHIPRIGHT_DOMAIN", domain);
            Add("SHIPRIGHT__PUBLIC_URL", $"https://{domain}");
            Add("SHIPRIGHT__CORS_ORIGINS", $"https://{domain}");
            Add("SHIPRIGHT__SMTP_FROM", $"no-reply@{domain}");
            if (string.IsNullOrWhiteSpace(filled.HealthCheckUrl))
                filled = filled with { HealthCheckUrl = $"https://{domain}/api/health" };
        }
        else
        {
            // No domain yet: the build compose serves the app over plain HTTP on 8080, reached by the address itself.
            var host = (filled.Host ?? string.Empty).Trim();
            if (host.Length > 0)
            {
                Add("SHIPRIGHT__PUBLIC_URL", $"http://{host}:8080");
                Add("SHIPRIGHT__CORS_ORIGINS", $"http://{host}:8080");
            }
            if (string.IsNullOrWhiteSpace(filled.HealthCheckUrl))
                filled = filled with { HealthCheckUrl = "http://localhost:5200/api/health" };
        }

        Add("SHIPRIGHT__SMTP_PORT", "587");
        Add("SHIPRIGHT__DB_CONNECTION",
            $"Server=db;Port=3306;Database={env["MYSQL_DATABASE"]};User Id={env["MYSQL_USER"]};Password={env["MYSQL_PASSWORD"]};Charset=utf8;ConvertZeroDateTime=True;");

        return filled with { Env = env };
    }

    private static string Secret() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
}
