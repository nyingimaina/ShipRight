using System.Text.RegularExpressions;
using ShipRight.Modules.Projects;
using ShipRight.Modules.Resources.Models;

namespace ShipRight.Modules.Transfer;

/// <summary>
/// Converts machine-specific locations into portable tokens on export and resolves them against the
/// destination root on import. Nothing written to a bundle keeps the drive, user or mount of its source.
/// </summary>
internal static partial class BundlePathRelocator
{
    public const string RootToken = "{ProjectRoot}";
    public const string ManagedKeyToken = "{ManagedKeys}";

    [GeneratedRegex(@"^[A-Za-z]:[\\/]")]
    private static partial Regex WindowsPath();

    [GeneratedRegex(@"^/mnt/([A-Za-z])/(.*)$")]
    private static partial Regex WslMount();

    public static string DetectRoot(ProjectConfig project)
    {
        var candidates = project.GitRepos.Select(g => g.RepoPath)
            .Concat(project.Services.Select(s => s.BuildContextPath))
            .Concat(project.Services.Select(s => s.VersionFilePath))
            .Where(IsAbsolutePath)
            .Select(Normalise)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p.Length)
            .ToList();
        if (candidates.Count == 0) return string.Empty;

        var root = candidates[0];
        while (candidates.Any(c => !c.Equals(root, StringComparison.OrdinalIgnoreCase) &&
                                   !c.StartsWith(root.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase)))
        {
            var slash = root.TrimEnd('/').LastIndexOf('/');
            if (slash <= 2) return string.Empty;
            root = root[..slash];
        }
        return root.TrimEnd('/');
    }

    public static bool IsAbsolutePath(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        (WindowsPath().IsMatch(path) || WslMount().IsMatch(path) || path.StartsWith("\\\\", StringComparison.Ordinal));

    /// <summary>Replaces one local path with a token. Paths outside the project root are dropped and reported as inputs the user must supply.</summary>
    public static string ToPortable(string? path, string root, BundlePortability portability, string pointer)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        var value = ToRootStyle(path, root);
        var normalised = Normalise(value);
        var basePath = Normalise(root).TrimEnd('/');

        if (!string.IsNullOrEmpty(basePath))
        {
            if (normalised.Equals(basePath, StringComparison.OrdinalIgnoreCase) ||
                normalised.StartsWith(basePath + "/", StringComparison.OrdinalIgnoreCase))
            {
                var relative = normalised.Equals(basePath, StringComparison.OrdinalIgnoreCase)
                    ? string.Empty
                    : normalised[(basePath.Length + 1)..];
                Track(portability.RelocatedPaths, pointer);
                return relative.Length == 0 ? RootToken : $"{RootToken}/{relative}";
            }

            // A compose checkout commonly sits beside the app checkout. Keep that relationship portable.
            var parent = basePath[..basePath.LastIndexOf('/')];
            if (parent.Length > 2 && normalised.StartsWith(parent + "/", StringComparison.OrdinalIgnoreCase))
            {
                var relative = "../" + normalised[(parent.Length + 1)..];
                Track(portability.RelocatedPaths, pointer);
                return $"{RootToken}/{relative}";
            }
        }

        Track(portability.RequiredInputs, pointer);
        return string.Empty;
    }

    public static string? FromPortable(string? value, string? targetRoot)
    {
        if (string.IsNullOrEmpty(value)) return value;
        if (targetRoot is null || !value.Contains(RootToken, StringComparison.Ordinal)) return value;
        var resolved = value.Replace(RootToken, targetRoot.TrimEnd('/', '\\'));
        return targetRoot.Contains('\\') ? resolved.Replace('/', '\\') : resolved;
    }

    /// <summary>Replaces a known root anywhere inside free-form script text, without echoing the source path.</summary>
    public static string ReplaceRootInText(string text, string root, BundlePortability portability, string pointer)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(root)) return text;
        var result = text;
        foreach (var variant in RootVariants(root))
            result = result.Replace(variant, RootToken, StringComparison.OrdinalIgnoreCase);
        if (!ReferenceEquals(result, text)) Track(portability.RelocatedPaths, pointer);
        return result;
    }

    public static ProjectConfig Relocate(ProjectConfig project, string root, BundlePortability portability, bool stripUrlCredentials = true) => project with
    {
        Services = project.Services.Select(s => s with
        {
            VersionFilePath = ToPortable(s.VersionFilePath, root, portability, $"project.services[{s.Name}].versionFile"),
            BuildContextPath = ToPortable(s.BuildContextPath, root, portability, $"project.services[{s.Name}].buildContext"),
            DockerfilePath = ToPortable(s.DockerfilePath, root, portability, $"project.services[{s.Name}].dockerfile"),
        }).ToList(),
        GitRepos = project.GitRepos.Select((g, i) => g with
        {
            RepoPath = ToPortable(g.RepoPath, root, portability, $"project.gitRepos[{i}].repoPath"),
        }).ToList(),
        Wsl = project.Wsl with
        {
            WorkingDir = ToPortable(project.Wsl.WorkingDir, root, portability, "project.wsl.workingDir"),
            ComposeRepoUrl = stripUrlCredentials ? BundleSecrets.StripUrlCredentials(project.Wsl.ComposeRepoUrl) : project.Wsl.ComposeRepoUrl,
        },
        Server = RelocateServerCopy(project.Server, portability, stripUrlCredentials),
    };

    public static ProjectConfig Resolve(ProjectConfig project, string? targetRoot) => project with
    {
        Services = project.Services.Select(s => s with
        {
            VersionFilePath = FromPortable(s.VersionFilePath, targetRoot) ?? string.Empty,
            BuildContextPath = FromPortable(s.BuildContextPath, targetRoot) ?? string.Empty,
            DockerfilePath = FromPortable(s.DockerfilePath, targetRoot) ?? string.Empty,
        }).ToList(),
        GitRepos = project.GitRepos.Select(g => g with { RepoPath = FromPortable(g.RepoPath, targetRoot) ?? string.Empty }).ToList(),
        Wsl = project.Wsl with { WorkingDir = FromPortable(project.Wsl.WorkingDir, targetRoot) ?? string.Empty },
        Server = ResolveServerCopy(project.Server, targetRoot),
    };

    public static ScriptResource Relocate(ScriptResource script, string root, BundlePortability portability) => script with
    {
        WorkingDirectory = ToPortable(script.WorkingDirectory, root, portability, $"script '{script.Name}'.workingDirectory"),
        Content = ReplaceRootInText(script.Content, root, portability, $"script '{script.Name}'.content"),
        Variables = script.Variables.ToDictionary(
            v => v.Key, v => ReplaceRootInText(v.Value, root, portability, $"script '{script.Name}'.variables.{v.Key}")),
    };

    public static ScriptResource Resolve(ScriptResource script, string? targetRoot) => script with
    {
        WorkingDirectory = FromPortable(script.WorkingDirectory, targetRoot),
        Content = FromPortable(script.Content, targetRoot) ?? string.Empty,
        Variables = script.Variables.ToDictionary(v => v.Key, v => FromPortable(v.Value, targetRoot) ?? string.Empty),
    };

    public static PipelineResource Relocate(PipelineResource pipeline, string root, BundlePortability portability) => pipeline with
    {
        Steps = pipeline.Steps.Select(s => s with
        {
            WorkingDirectory = ToPortable(s.WorkingDirectory, root, portability, $"pipeline '{pipeline.Name}'.stepWorkingDirectory"),
        }).ToList(),
        Variables = pipeline.Variables.ToDictionary(
            v => v.Key, v => ReplaceRootInText(v.Value, root, portability, $"pipeline '{pipeline.Name}'.variables.{v.Key}")),
    };

    public static PipelineResource Resolve(PipelineResource pipeline, string? targetRoot) => pipeline with
    {
        Steps = pipeline.Steps.Select(s => s with { WorkingDirectory = FromPortable(s.WorkingDirectory, targetRoot) }).ToList(),
        Variables = pipeline.Variables.ToDictionary(v => v.Key, v => FromPortable(v.Value, targetRoot) ?? string.Empty),
    };

    public static ServerConfig Relocate(ServerConfig server, string root, BundlePortability portability, bool stripUrlCredentials = true) =>
        RelocateServerCopy(server, portability, stripUrlCredentials);

    private static ServerConfig RelocateServerCopy(ServerConfig server, BundlePortability portability, bool stripUrlCredentials = true) => server with
    {
        SshKeyPath = server.ManagedSshKey ? ManagedKeyToken : string.Empty,
        ComposeRepoUrl = stripUrlCredentials ? BundleSecrets.StripUrlCredentials(server.ComposeRepoUrl) : server.ComposeRepoUrl,
    };

    private static ServerConfig ResolveServerCopy(ServerConfig server, string? targetRoot) => server with
    {
        SshKeyPath = string.Empty,
    };

    private static IEnumerable<string> RootVariants(string root)
    {
        yield return root;
        yield return Normalise(root);
        var wsl = WslEquivalent(root);
        if (!string.IsNullOrEmpty(wsl)) yield return wsl;
    }

    private static string ToRootStyle(string path, string root)
    {
        var match = WslMount().Match(path);
        if (match.Success && WindowsPath().IsMatch(root))
            return $"{match.Groups[1].Value}:\\{match.Groups[2].Value.Replace('/', '\\')}";
        return path;
    }

    private static string? WslEquivalent(string path)
    {
        var match = WindowsPath().IsMatch(path) ? Regex.Match(path, @"^([A-Za-z]):[\\/](.*)$") : Match.Empty;
        return match.Success ? $"/mnt/{char.ToLowerInvariant(match.Groups[1].Value[0])}/{match.Groups[2].Value.Replace('\\', '/')}" : null;
    }

    private static string Normalise(string path) => path.Replace('\\', '/').TrimEnd('/');

    private static void Track(List<string> items, string item)
    {
        if (!items.Contains(item, StringComparer.Ordinal)) items.Add(item);
    }
}
