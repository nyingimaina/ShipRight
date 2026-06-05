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

    public async Task<ProcessResult> RunAsync(
        string executable,
        string[] args,
        string? workingDir,
        Func<string, Task>? onOutput = null,
        Func<string, Task>? onError = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var stdOutBuf = new StringBuilder();
        var stdErrBuf = new StringBuilder();

        var psi = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDir ?? string.Empty,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        Log.Information("→ {Executable} {Args}  (workdir: {WorkingDir})",
            executable, string.Join(' ', args), workingDir ?? "(inherit)");

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

        await Task.WhenAll(
            process.WaitForExitAsync(ct),
            outputDone.Task,
            errorDone.Task);

        sw.Stop();
        var result = new ProcessResult(process.ExitCode, stdOutBuf.ToString(), stdErrBuf.ToString(), sw.Elapsed);

        if (result.Success)
            Log.Information("✓ {Executable} exited 0 in {Ms}ms", executable, sw.ElapsedMilliseconds);
        else
            Log.Error("Process failed {ExitCode} in {Ms}ms: {Executable} {Args}\n{StdErr}",
                result.ExitCode, sw.ElapsedMilliseconds, executable,
                string.Join(' ', args),
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
