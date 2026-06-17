using System.Diagnostics;
using System.Text;
using Serilog;

namespace ShipRight.Shared.ProcessRunner;

public class ProcessRunner : IProcessRunner
{
    private static readonly HashSet<string> _redactPatterns =
    [
        "password", "-----begin", "secret", "token"
    ];

    // On Windows, docker and git run inside WSL — the executables don't exist on the Windows PATH
    private static readonly bool _isWindows = OperatingSystem.IsWindows();
    private static readonly HashSet<string> _wslCommands = ["docker", "git"];

    public static (string Executable, string[] Args) ResolveForPlatform(string executable, string[] args)
    {
        if (_isWindows && _wslCommands.Contains(executable))
        {
            var wslArgs = new[] { executable }.Concat(args.Select(ToWslPath)).ToArray();
            return ("wsl", wslArgs);
        }
        return (executable, args);
    }

    // Converts Windows absolute paths (D:\foo\bar) to WSL mount paths (/mnt/d/foo/bar).
    // Non-path args are returned unchanged.
    public static string ToWslPath(string arg)
    {
        if (arg.Length >= 3 && char.IsLetter(arg[0]) && arg[1] == ':' && (arg[2] == '\\' || arg[2] == '/'))
        {
            var drive = char.ToLowerInvariant(arg[0]);
            var rest = arg[3..].Replace('\\', '/');
            return $"/mnt/{drive}/{rest}";
        }
        return arg;
    }

    public async Task<ProcessResult> RunAsync(
        string executable,
        string[] args,
        string? workingDir,
        Func<string, Task>? onOutput = null,
        Func<string, Task>? onError = null,
        CancellationToken ct = default,
        IReadOnlyDictionary<string, string>? envOverride = null)
    {
        var sw = Stopwatch.StartNew();
        var stdOutBuf = new StringBuilder();
        var stdErrBuf = new StringBuilder();

        var (resolvedExe, resolvedArgs) = ResolveForPlatform(executable, args);

        // Inject env vars. For WSL-wrapped commands, prepend `env VAR=val` so the Linux
        // process sees them regardless of WSLENV passthrough settings.
        if (envOverride is { Count: > 0 })
        {
            var envPairs = envOverride.Select(kv => $"{kv.Key}={kv.Value}").ToArray();
            resolvedArgs = resolvedExe == "wsl"
                ? new[] { "env" }.Concat(envPairs).Concat(resolvedArgs).ToArray()
                : resolvedArgs;
        }

        var psi = new ProcessStartInfo
        {
            FileName = resolvedExe,
            WorkingDirectory = workingDir ?? string.Empty,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in resolvedArgs) psi.ArgumentList.Add(arg);

        // For non-WSL processes, inject env vars via StartInfo.EnvironmentVariables.
        if (envOverride is { Count: > 0 } && resolvedExe != "wsl")
        {
            foreach (var (k, v) in envOverride)
                psi.EnvironmentVariables[k] = v;
        }

        Log.Information("→ {Executable} {Args}  (workdir: {WorkingDir})",
            resolvedExe, string.Join(' ', resolvedArgs), workingDir ?? "(inherit)");

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var outputDone = new TaskCompletionSource<bool>();
        var errorDone  = new TaskCompletionSource<bool>();

        process.OutputDataReceived += async (_, e) =>
        {
            if (e.Data is null) { outputDone.TrySetResult(true); return; }
            var line = Redact(e.Data);
            stdOutBuf.AppendLine(line);
            if (onOutput is not null)
                try { await onOutput(line); } catch { }
        };

        process.ErrorDataReceived += async (_, e) =>
        {
            if (e.Data is null) { errorDone.TrySetResult(true); return; }
            var line = Redact(e.Data);
            stdErrBuf.AppendLine(line);
            if (onError is not null)
                try { await onError(line); } catch { }
            else if (onOutput is not null)
                try { await onOutput(line); } catch { }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await Task.WhenAll(
                process.WaitForExitAsync(ct),
                outputDone.Task,
                errorDone.Task);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            try { await Task.WhenAll(outputDone.Task, errorDone.Task).WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
            throw;
        }

        sw.Stop();
        var result = new ProcessResult(process.ExitCode, stdOutBuf.ToString(), stdErrBuf.ToString(), sw.Elapsed);

        if (result.Success)
            Log.Information("✓ {Executable} exited 0 in {Ms}ms", resolvedExe, sw.ElapsedMilliseconds);
        else
            Log.Error("Process failed {ExitCode} in {Ms}ms: {Executable} {Args}\n{StdErr}",
                result.ExitCode, sw.ElapsedMilliseconds, resolvedExe,
                string.Join(' ', resolvedArgs),
                TailLines(result.StdErr, 50));

        return result;
    }

    private static string Redact(string line)
    {
        var lower = line.ToLowerInvariant();
        return _redactPatterns.Any(p => lower.Contains(p)) ? "[REDACTED]" : line;
    }

    private static string TailLines(string text, int n)
    {
        var lines = text.Split('\n');
        return string.Join('\n', lines.TakeLast(n));
    }
}
