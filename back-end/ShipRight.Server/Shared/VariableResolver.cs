using System.Text.RegularExpressions;
using ShipRight.Modules.Projects;
using ShipRight.Shared.ProcessRunner;

namespace ShipRight.Shared;

public static partial class VariableResolver
{
    private static readonly Regex _variablePattern = VariablePattern();

    /// <summary>
    /// Resolves {VariableName} placeholders in a string using built-in and custom variables.
    /// Built-in variables are resolved first, then custom variables (allowing custom vars to reference built-ins).
    /// Unresolved placeholders are left as-is.
    /// </summary>
    public static string Resolve(
        string input,
        Dictionary<string, string> customVariables,
        ProjectConfig project,
        string? scriptDir = null)
    {
        if (string.IsNullOrEmpty(input))
            return input;

        var builtins = BuildBuiltinVariables(project, scriptDir);
        var allVariables = new Dictionary<string, string>(builtins, StringComparer.OrdinalIgnoreCase);

        // Custom vars can reference built-ins, so resolve them in order
        foreach (var kv in customVariables)
        {
            var value = ResolveSinglePass(kv.Value, allVariables);
            allVariables[kv.Key] = value;
        }

        // Final resolution pass with all variables available
        return ResolveSinglePass(input, allVariables);
    }

    /// <summary>
    /// Resolves the working directory for a pipeline step.
    /// Falls back to the project's WSL working directory if no custom directory is specified.
    /// </summary>
    public static string ResolveWorkingDirectory(
        string? stepWorkingDir,
        Dictionary<string, string> customVariables,
        ProjectConfig project,
        string? scriptDir = null)
    {
        var raw = string.IsNullOrWhiteSpace(stepWorkingDir)
            ? project.Wsl.WorkingDir
            : stepWorkingDir;

        return Resolve(raw, customVariables, project, scriptDir);
    }

    /// <summary>
    /// Converts a path for the current execution context.
    /// On Windows with WSL-wrapped commands, paths should remain as WSL paths.
    /// On Windows with native commands, WSL paths are converted to Windows paths.
    /// On Linux, paths are returned as-is.
    /// </summary>
    public static string ConvertPathForContext(string path, bool isWslContext)
    {
        if (isWslContext)
            return path;

        if (!OperatingSystem.IsWindows())
            return path;

        // If we're running natively on Windows and the path looks like a WSL path,
        // try to convert it to a Windows path
        if (path.StartsWith('/') && !path.StartsWith("/tmp"))
            return FromWslPath(path);

        return path;
    }

    /// <summary>
    /// Converts a WSL path (/mnt/d/foo/bar) to a Windows path (D:\foo\bar).
    /// </summary>
    public static string FromWslPath(string wslPath)
    {
        // /mnt/d/... → D:\...
        if (wslPath.StartsWith("/mnt/") && wslPath.Length >= 6 && char.IsLetter(wslPath[5]) && (wslPath.Length == 6 || wslPath[6] == '/'))
        {
            var drive = char.ToUpperInvariant(wslPath[5]);
            var rest = wslPath[6..].Replace('/', '\\');
            return $"{drive}:{rest}";
        }

        // /home/... → \\wsl$\Ubuntu\home\...
        var normalized = wslPath.Replace('/', '\\');
        return $"\\\\wsl$\\Ubuntu{normalized}";
    }

    /// <summary>
    /// Determines if the given shell command will run under WSL on Windows.
    /// </summary>
    public static bool IsWslCommand(string shellCommand, bool isWindows)
    {
        if (!isWindows)
            return false;

        return shellCommand is "bash" or "sh" or "python3";
    }

    private static string ResolveSinglePass(string input, Dictionary<string, string> variables)
    {
        return _variablePattern.Replace(input, match =>
        {
            var name = match.Groups[1].Value;
            return variables.TryGetValue(name, out var value) ? value : match.Value;
        });
    }

    private static Dictionary<string, string> BuildBuiltinVariables(ProjectConfig project, string? scriptDir)
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ProjectDir"] = project.Wsl.WorkingDir,
            ["ProjectName"] = project.Name,
            ["ProjectId"] = project.Id,
            ["ScriptDir"] = scriptDir ?? Path.GetTempPath(),
            ["TempDir"] = Path.GetTempPath(),
        };
    }

    [GeneratedRegex(@"\{([A-Za-z_][A-Za-z0-9_]*)\}")]
    private static partial Regex VariablePattern();
}
