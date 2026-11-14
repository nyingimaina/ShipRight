using Newtonsoft.Json;

namespace ShipRight.Modules.JitDeploy;

/// <summary>"When <see cref="Service"/> is replaced, also restart <see cref="Dependent"/>?" The user's answer for one pair.</summary>
public sealed record JitDependentChoice(string Service, string Dependent, bool Restart);

/// <summary>
/// A project's build-on-server settings. Kept apart from the project itself, so nothing about an existing project
/// changes, and saving a project from any screen can never lose or alter these. Off by default.
/// </summary>
public sealed record JitDeployConfig
{
    public bool Enabled { get; init; }
    /// <summary>The git address the server fetches the code from.</summary>
    public string RepoUrl { get; init; } = string.Empty;
    public string Branch { get; init; } = "master";
    /// <summary>Optional. Blank derives one from the project name.</summary>
    public string ComposeProject { get; init; } = string.Empty;
    /// <summary>Optional absolute path of an env file on the server, passed to compose.</summary>
    public string EnvFile { get; init; } = string.Empty;
    public int ReleasesToKeep { get; init; } = 3;
    public int StopGraceSeconds { get; init; } = 30;
    public int HealthTimeoutSeconds { get; init; } = 60;
    public int SettleSeconds { get; init; } = 10;
    public int MinFreeDiskMb { get; init; } = 3000;
    public int? BuildNice { get; init; }
    /// <summary>Use a read-only key made on the server (never a password or token) to read the repository.</summary>
    public bool UseDeployKey { get; init; }
    /// <summary>A saved credential (access token) to read a private https repository. Never part of the address.</summary>
    public Guid? CredentialResourceId { get; init; }
    /// <summary>Path of the compose file inside the repository. Empty = the usual file in the repository's top folder.</summary>
    public string ComposeFile { get; init; } = string.Empty;
    /// <summary>The services to build, taken from the compose file, for a project that has none of its own.</summary>
    public IReadOnlyList<JitConfiguredService> ComposeServices { get; init; } = [];
    public IReadOnlyList<JitDependentChoice> DependentChoices { get; init; } = [];
}

public interface IJitConfigStore
{
    Task<JitDeployConfig?> GetAsync(string projectId);
    Task SaveAsync(string projectId, JitDeployConfig config);
    Task DeleteAsync(string projectId);
}

/// <summary>
/// All projects' settings in one small JSON file in the data folder. Writes are atomic (temp file, then rename). A
/// damaged file is an error that leaves the file alone: it is never replaced with an empty one.
/// </summary>
public sealed class JsonJitConfigStore : IJitConfigStore
{
    public const string FileName = "jit-deploy.json";

    private readonly string _dir;
    private readonly string _file;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonJitConfigStore(string dataDir)
    {
        _dir = dataDir;
        _file = Path.Combine(dataDir, FileName);
    }

    public async Task<JitDeployConfig?> GetAsync(string projectId)
    {
        await _gate.WaitAsync();
        try { return (await LoadAsync()).GetValueOrDefault(projectId); }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(string projectId, JitDeployConfig config)
    {
        await _gate.WaitAsync();
        try
        {
            var all = await LoadAsync();
            all[projectId] = config;
            await WriteAsync(all);
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteAsync(string projectId)
    {
        await _gate.WaitAsync();
        try
        {
            var all = await LoadAsync();
            if (all.Remove(projectId)) await WriteAsync(all);
        }
        finally { _gate.Release(); }
    }

    private async Task<Dictionary<string, JitDeployConfig>> LoadAsync()
    {
        if (!File.Exists(_file)) return new Dictionary<string, JitDeployConfig>(StringComparer.Ordinal);
        var text = await File.ReadAllTextAsync(_file);
        try
        {
            return JsonConvert.DeserializeObject<Dictionary<string, JitDeployConfig>>(text)
                   ?? throw new InvalidDataException("it is empty");
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            throw new InvalidDataException($"The build-on-server settings file ({_file}) is damaged and was left untouched: {ex.Message}");
        }
    }

    private async Task WriteAsync(Dictionary<string, JitDeployConfig> all)
    {
        Directory.CreateDirectory(_dir);
        var tmp = _file + ".tmp";
        await File.WriteAllTextAsync(tmp, JsonConvert.SerializeObject(all, Formatting.Indented));
        File.Move(tmp, _file, overwrite: true);
    }
}

/// <summary>A service build-on-server builds, named in the settings (from the compose file) instead of in the project.</summary>
public sealed record JitConfiguredService(
    string Name, string ImageRef, string TagVariable,
    string? Dockerfile = null, string? Context = null, string? Target = null, Dictionary<string, string>? Args = null);

public sealed record JitConfigProblem(string Message, string Field);

public static class JitDeployConfigValidator
{
    public static IReadOnlyList<JitConfigProblem> Validate(JitDeployConfig c)
    {
        var p = new List<JitConfigProblem>();

        void Safe(string field, string what, Func<string, string> check, string value)
        {
            try { check(value); }
            catch (ArgumentException ex) { p.Add(new($"The {what} is not valid. {ex.Message}", field)); }
        }
        void Range(string field, string what, int value, int min, int max)
        {
            if (value < min || value > max) p.Add(new($"The {what} must be between {min} and {max}.", field));
        }

        if (string.IsNullOrWhiteSpace(c.Branch))
        {
            if (c.Enabled) p.Add(new("Enter the branch the server should deploy (for example master).", "branch"));
        }
        else Safe("branch", "branch", JitNames.Branch, c.Branch);

        if (string.IsNullOrWhiteSpace(c.RepoUrl))
        {
            if (c.Enabled) p.Add(new("Enter the git address the server should fetch your code from.", "repoUrl"));
        }
        else Safe("repoUrl", "git address", JitNames.RepoUrl, c.RepoUrl);

        foreach (var s in c.ComposeServices ?? [])
        {
            Safe("composeServices", "service name", JitNames.Service, s.Name ?? string.Empty);
            Safe("composeServices", "image name", JitNames.ImageRef, s.ImageRef ?? string.Empty);
            Safe("composeServices", "tag variable", JitNames.TagVariable, s.TagVariable ?? string.Empty);
            if (string.IsNullOrWhiteSpace(s.Dockerfile)) p.Add(new($"Pick the Dockerfile for '{s.Name}'.", "composeServices"));
            else if (ComposeOverride.Problem("service", new JitBuildRecipe(string.IsNullOrWhiteSpace(s.Context) ? "." : s.Context!.Trim(), s.Dockerfile!.Trim(), s.Target, s.Args)) is { } recipeProblem)
                p.Add(new($"The build instructions for '{s.Name}' are not valid. {recipeProblem}", "composeServices"));
        }
        if ((c.ComposeServices ?? []).GroupBy(s => s.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
            p.Add(new("Each service can only be listed once.", "composeServices"));
        if (c.UseDeployKey && c.CredentialResourceId is not null)
            p.Add(new("Choose either a deploy key or a saved credential, not both.", "credentialResourceId"));
        else if (c.CredentialResourceId is not null && !string.IsNullOrWhiteSpace(c.RepoUrl) && !global::System.Text.RegularExpressions.Regex.IsMatch(c.RepoUrl.Trim(), "^https://", global::System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            p.Add(new("A saved credential only works with an https address. Use the SSH form with a deploy key instead.", "credentialResourceId"));
        if (!string.IsNullOrWhiteSpace(c.ComposeFile)) Safe("composeFile", "compose file", JitNames.RepoFile, c.ComposeFile.Trim());
        if (!string.IsNullOrWhiteSpace(c.ComposeProject)) Safe("composeProject", "compose project name", JitNames.Project, c.ComposeProject);
        if (!string.IsNullOrWhiteSpace(c.EnvFile)) Safe("envFile", "env file path", JitNames.Path, c.EnvFile);

        Range("releasesToKeep", "number of releases to keep", c.ReleasesToKeep, 0, 100);
        Range("stopGraceSeconds", "stop grace period (seconds)", c.StopGraceSeconds, 0, 600);
        Range("healthTimeoutSeconds", "health check timeout (seconds)", c.HealthTimeoutSeconds, 5, 3600);
        Range("settleSeconds", "settle time (seconds)", c.SettleSeconds, 0, 600);
        Range("minFreeDiskMb", "minimum free disk space (MB)", c.MinFreeDiskMb, 100, 1_000_000);
        if (c.BuildNice is { } nice) Range("buildNice", "build priority", nice, 0, 19);

        foreach (var d in c.DependentChoices ?? [])
        {
            Safe("dependentChoices", "service name in a restart choice", JitNames.Service, d.Service ?? string.Empty);
            Safe("dependentChoices", "service name in a restart choice", JitNames.Service, d.Dependent ?? string.Empty);
        }
        return p;
    }
}
