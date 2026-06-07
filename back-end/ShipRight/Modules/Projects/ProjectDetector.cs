using System.Text.RegularExpressions;
using Serilog;

namespace ShipRight.Modules.Projects;

public record DetectedService(
    string SuggestedName,
    string VersionFilePath,
    string BuildContextPath,
    string? DockerImageName,        // null if not detected
    bool ImageDetected,
    string? ComposeServiceName);    // service key in docker-compose.yml, null if not detected

public record DetectedGitRepo(string RepoPath, string DeployBranch);

public record DetectedProjectConfig(
    string? SuggestedName,
    List<DetectedService> Services,
    List<DetectedGitRepo> GitRepos,
    string? WslWorkingDir,      // path to directory containing docker-compose.yml
    List<string> Detected,      // human-readable list of what was found
    List<string> Undetected);   // human-readable list of what still needs manual entry

public static class ProjectDetector
{
    public static DetectedProjectConfig Detect(string rootPath)
    {
        Log.Information("Project detection starting at {RootPath}", rootPath);

        var detected = new List<string>();
        var undetected = new List<string>();

        // ── Suggested name from directory ────────────────────────────────────
        var suggestedName = Path.GetFileName(rootPath.TrimEnd(Path.DirectorySeparatorChar))
            ?.Replace('-', ' ')
            ?.Replace('_', ' ')
            ?? null;

        // ── Find version.txt files (up to 3 levels deep) ────────────────────
        var versionFiles = FindFiles(rootPath, "version.txt", maxDepth: 3);
        Log.Information("Found {Count} version.txt files", versionFiles.Count);

        // ── Find Dockerfiles ─────────────────────────────────────────────────
        var dockerfiles = FindFiles(rootPath, "dockerfile", maxDepth: 3, caseInsensitive: true);
        Log.Information("Found {Count} Dockerfiles", dockerfiles.Count);

        // ── Find docker-compose.yml (root or 1 level up/sideways) ────────────
        var composePaths = new List<string>();
        composePaths.AddRange(FindFiles(rootPath, "docker-compose.yml", maxDepth: 1));
        var parent = Path.GetDirectoryName(rootPath);
        if (parent is not null)
        {
            foreach (var sibling in Directory.GetDirectories(parent))
                composePaths.AddRange(FindFiles(sibling, "docker-compose.yml", maxDepth: 1));
        }
        Log.Information("Found {Count} docker-compose.yml files", composePaths.Count);

        // Parse compose files for image names (map serviceDirKeyword → imageName)
        var composeImageMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? wslWorkingDir = null;
        foreach (var cp in composePaths)
        {
            var images = ParseDockerComposeImages(cp);
            foreach (var (key, val) in images) composeImageMap[key] = val;
            if (wslWorkingDir is null && images.Count > 0)
                wslWorkingDir = Path.GetDirectoryName(cp);
        }

        // ── Pair version.txt with its nearest Dockerfile ─────────────────────
        var services = new List<DetectedService>();
        foreach (var vf in versionFiles)
        {
            var vDir = Path.GetDirectoryName(vf)!;

            // Dockerfile in same dir, or parent dir
            var dockerfile = dockerfiles.FirstOrDefault(df =>
                Path.GetDirectoryName(df) == vDir ||
                Path.GetDirectoryName(df) == Path.GetDirectoryName(vDir));

            if (dockerfile is null) continue;

            var buildContext = Path.GetDirectoryName(dockerfile)!;
            var dirName = Path.GetFileName(buildContext) ?? "service";

            // Suggest service name from directory name (clean it up)
            var svcName = ToServiceName(dirName);

            // Try to find image name and compose service name from compose file
            var composeMatch = composeImageMap
                .Where(kv => dirName.Contains(kv.Key, StringComparison.OrdinalIgnoreCase)
                          || kv.Key.Contains(dirName, StringComparison.OrdinalIgnoreCase))
                .Cast<KeyValuePair<string, string>?>()
                .FirstOrDefault();

            string? imageName = composeMatch?.Value;
            string? composeServiceName = composeMatch?.Key;

            services.Add(new DetectedService(svcName, vf, buildContext, imageName, imageName is not null, composeServiceName));
            Log.Information("Service detected: {Name} | version: {Vf} | context: {Ctx} | image: {Img} | compose: {Compose}",
                svcName, vf, buildContext, imageName ?? "(not found)", composeServiceName ?? "(not found)");
        }

        // ── Git detection — find all unique .git roots ────────────────────────
        var gitRepoPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Walk up from the root path itself
        var rootGit = FindGitRoot(rootPath);
        if (rootGit is not null) gitRepoPaths.Add(rootGit);

        // 2. Walk up from each service's build context (catches per-service repos)
        foreach (var svc in services)
        {
            var svcGit = FindGitRoot(svc.BuildContextPath);
            if (svcGit is not null) gitRepoPaths.Add(svcGit);
        }

        var gitRepos = new List<DetectedGitRepo>();
        foreach (var repoPath in gitRepoPaths)
        {
            var branch = DetectDefaultBranch(repoPath) ?? "master";
            gitRepos.Add(new DetectedGitRepo(repoPath, branch));
            Log.Information("Git repo detected: {Path}, branch: {Branch}", repoPath, branch);
        }

        if (gitRepos.Count > 0)
            detected.Add($"{gitRepos.Count} git repo(s) detected");
        else
            undetected.Add("Git repository path(s)");

        // ── Summarise ────────────────────────────────────────────────────────
        if (services.Count > 0)
            detected.Add($"{services.Count} service(s) detected");
        else
            undetected.Add("Services (no version.txt + Dockerfile pairs found)");

        if (wslWorkingDir is not null)
            detected.Add($"Docker-compose directory: {wslWorkingDir}");
        else
            undetected.Add("WSL working directory (docker-compose location)");

        foreach (var svc in services.Where(s => !s.ImageDetected))
            undetected.Add($"Docker image name for service '{svc.SuggestedName}'");

        undetected.Add("Server host");
        undetected.Add("SSH key path");
        undetected.Add("Remote working directory");

        return new DetectedProjectConfig(
            suggestedName, services, gitRepos,
            wslWorkingDir, detected, undetected);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static List<string> FindFiles(string root, string name, int maxDepth, bool caseInsensitive = false)
    {
        var results = new List<string>();
        if (!Directory.Exists(root)) return results;
        var comparison = caseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        WalkDir(root, 0, maxDepth, entry =>
        {
            if (string.Equals(Path.GetFileName(entry), name, comparison))
                results.Add(entry);
        });
        return results;
    }

    private static void WalkDir(string dir, int depth, int maxDepth, Action<string> onFile)
    {
        if (depth > maxDepth) return;
        try
        {
            foreach (var f in Directory.GetFiles(dir)) onFile(f);
            if (depth < maxDepth)
                foreach (var d in Directory.GetDirectories(dir))
                    if (!Path.GetFileName(d).StartsWith('.')) // skip .git etc.
                        WalkDir(d, depth + 1, maxDepth, onFile);
        }
        catch (UnauthorizedAccessException) { }
    }

    private static string? FindGitRoot(string path)
    {
        var current = path;
        for (int i = 0; i < 5; i++)
        {
            if (Directory.Exists(Path.Combine(current, ".git"))) return current;
            var parent = Path.GetDirectoryName(current);
            if (parent is null || parent == current) break;
            current = parent;
        }
        return null;
    }

    private static string? DetectDefaultBranch(string repoPath)
    {
        // Try HEAD file: e.g. "ref: refs/heads/master"
        var headFile = Path.Combine(repoPath, ".git", "HEAD");
        if (File.Exists(headFile))
        {
            var content = File.ReadAllText(headFile).Trim();
            var m = Regex.Match(content, @"refs/heads/(.+)");
            if (m.Success) return m.Groups[1].Value;
        }
        return null;
    }

    private static readonly Regex ImageLineRegex =
        new(@"^\s*image:\s*[""']?([a-z0-9._\-\/]+:[a-z0-9._\-]+|[a-z0-9._\-\/]+)[""']?\s*$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ServiceNameRegex =
        new(@"^(\w[\w\-]*)\s*:", RegexOptions.Compiled);

    private static Dictionary<string, string> ParseDockerComposeImages(string composePath)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var lines = File.ReadAllLines(composePath);
            string? currentService = null;
            foreach (var line in lines)
            {
                // Detect service block (2-space indented service name)
                var svcMatch = Regex.Match(line, @"^  (\w[\w\-]*):\s*$");
                if (svcMatch.Success) { currentService = svcMatch.Groups[1].Value; continue; }

                // Detect image line under current service
                if (currentService is not null)
                {
                    var imgMatch = Regex.Match(line, @"^\s+image:\s*[""']?([^""'\s:]+)(?::([^\s""']+))?[""']?");
                    if (imgMatch.Success)
                    {
                        var imageName = imgMatch.Groups[1].Value;
                        result[currentService] = imageName;
                        Log.Debug("Compose: service={Svc} image={Img}", currentService, imageName);
                    }
                }
            }
        }
        catch (Exception ex) { Log.Debug(ex, "Could not parse {Path}", composePath); }
        return result;
    }

    private static string ToServiceName(string dirName)
    {
        // jattac.app.sms.gateway → SMS Gateway, jattac-sms-web-ui → Web UI
        var parts = dirName
            .Split(new[] { '.', '-', '_' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p.Length > 2 && !new[] { "jattac", "app", "libs", "web" }.Contains(p.ToLower()))
            .Select(p => char.ToUpper(p[0]) + p[1..])
            .ToList();
        return parts.Count > 0 ? string.Join(" ", parts) : dirName;
    }
}
