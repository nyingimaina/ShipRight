using ShipRight.Modules.Resources.Models;
using ShipRight.Modules.Projects;
using ShipRight.Shared.ProcessRunner;

namespace ShipRight.Modules.Resources;

public class ScriptExecutor
{
    private readonly IProcessRunner _runner;

    public ScriptExecutor(IProcessRunner runner)
    {
        _runner = runner;
    }

    public static string GetShellExtension(ScriptPlatform platform) => platform switch
    {
        ScriptPlatform.Bash => ".sh",
        ScriptPlatform.PowerShell => ".ps1",
        ScriptPlatform.Cmd => ".cmd",
        ScriptPlatform.Python => ".py",
        ScriptPlatform.Sh => ".sh",
        _ => ".sh",
    };

    public static string GetShellCommand(ScriptPlatform platform) => platform switch
    {
        ScriptPlatform.Bash => "bash",
        ScriptPlatform.PowerShell => "pwsh",
        ScriptPlatform.Cmd => "cmd",
        ScriptPlatform.Python => "python3",
        ScriptPlatform.Sh => "sh",
        _ => "bash",
    };

    public static string[] BuildArgs(ScriptPlatform platform, string scriptPath) => platform switch
    {
        ScriptPlatform.Bash => [scriptPath],
        ScriptPlatform.Sh => [scriptPath],
        ScriptPlatform.Cmd => ["/c", scriptPath],
        ScriptPlatform.PowerShell => ["-File", scriptPath],
        ScriptPlatform.Python => [scriptPath],
        _ => [scriptPath],
    };

    public static string GetTargetDirectory(ExecutionTarget target, ServerConfig? serverConfig) => target switch
    {
        ExecutionTarget.Remote when serverConfig is not null =>
            $"/tmp/shipright_scripts_{Guid.NewGuid():N}",
        _ => Path.Combine(Path.GetTempPath(), $"shipright_scripts_{Guid.NewGuid():N}"),
    };

    public async Task<ScriptResult> ExecuteAsync(
        ScriptResource script,
        string workingDir,
        Dictionary<string, string>? envVars = null,
        ServerConfig? serverConfig = null,
        Func<string, Task>? onOutput = null,
        CancellationToken ct = default)
    {
        var scriptDir = GetTargetDirectory(script.Target, serverConfig);
        var ext = GetShellExtension(script.Platform);
        var scriptPath = Path.Combine(scriptDir, $"script{ext}");

        if (script.Target == ExecutionTarget.Local)
        {
            Directory.CreateDirectory(scriptDir);
            await File.WriteAllTextAsync(scriptPath, script.Content, ct);

            try
            {
                var shell = GetShellCommand(script.Platform);
                var args = BuildArgs(script.Platform, scriptPath);
                var result = await _runner.RunAsync(
                    shell,
                    args,
                    workingDir,
                    onOutput,
                    onOutput,
                    ct,
                    envVars);

                return new ScriptResult
                {
                    Success = result.Success,
                    ExitCode = result.ExitCode,
                    Output = result.StdOut,
                    Error = result.StdErr,
                    Duration = result.Duration,
                };
            }
            finally
            {
                try { Directory.Delete(scriptDir, recursive: true); } catch { }
            }
        }
        else
        {
            throw new NotImplementedException("Remote execution not yet implemented");
        }
    }
}

public class ScriptResult
{
    public bool Success { get; init; }
    public int ExitCode { get; init; }
    public string Output { get; init; } = string.Empty;
    public string Error { get; init; } = string.Empty;
    public TimeSpan Duration { get; init; }
}
