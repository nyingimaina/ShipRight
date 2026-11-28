using System.Diagnostics;
using System.Text.Json;
using Serilog;

namespace ShipRight.Modules.Notifications;

/// <summary>What the host can see about Telegram without revealing the token.</summary>
public sealed record TelegramNotificationSettings(string CliPath, bool TokenConfigured, bool ChatConfigured);

public sealed record TelegramCliResult(bool Ok, int? Parts, string? ErrorKind, string? ErrorMessage);

/// <summary>
/// Sends notifications through the standalone <c>telegram-notify</c> CLI. The token and chat id are inherited from the
/// environment, never put on a command line, and never logged. A notification failure is reported, not thrown: it
/// must not block a deploy or a scan.
/// </summary>
public sealed class TelegramCliNotifier
{
    private readonly string _cliPath;

    public TelegramCliNotifier()
    {
        _cliPath = Environment.GetEnvironmentVariable("SHIPRIGHT__TELEGRAM__CLI") is { Length: > 0 } path
            ? path
            : "telegram-notify";
    }

    public TelegramNotificationSettings Settings => new(
        _cliPath,
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN")),
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TELEGRAM_CHAT_ID")));

    public async Task<TelegramCliResult> SendAsync(string text, CancellationToken ct = default)
    {
        var settings = Settings;
        if (!settings.TokenConfigured)
            return new(false, null, "Config", "TELEGRAM_BOT_TOKEN is not set for this process.");
        if (!settings.ChatConfigured)
            return new(false, null, "Config", "TELEGRAM_CHAT_ID is not set for this process.");
        if (string.IsNullOrWhiteSpace(text))
            return new(false, null, "Usage", "There is nothing to send.");

        var start = StartInfo(_cliPath);
        start.ArgumentList.Add("send");
        start.ArgumentList.Add("--json");
        start.ArgumentList.Add("--stdin");

        try
        {
            using var process = Process.Start(start);
            if (process is null)
                return new(false, null, "Config", "The telegram-notify CLI could not be started.");

            await process.StandardInput.WriteAsync(text);
            process.StandardInput.Close();

            // Read both pipes before waiting: a full pipe can deadlock a process that is waited on first.
            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(120));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return new(false, null, "Network", "The Telegram notifier did not finish in time. Try again.");
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (process.ExitCode == 0)
            {
                var parts = ParseParts(stdout);
                return new(true, parts, null, null);
            }

            var (kind, message) = ParseError(stdout, stderr);
            Log.Warning("Telegram notification failed: {Kind} {Message}", kind, message);
            return new(false, null, kind, message);
        }
        catch (Exception ex) when (ex is global::System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new(false, null, "Config",
                $"The telegram-notify CLI could not be started. Check SHIPRIGHT__TELEGRAM__CLI ({ex.Message}).");
        }
    }

    private static ProcessStartInfo StartInfo(string cliPath)
    {
        var start = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        // A framework-dependent CLI is a .dll; a published one is an executable.
        if (cliPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            start.FileName = "dotnet";
            start.ArgumentList.Add(cliPath);
        }
        else
        {
            start.FileName = cliPath;
        }

        return start;
    }

    private static int? ParseParts(string stdout)
    {
        try
        {
            using var doc = JsonDocument.Parse(stdout);
            return doc.RootElement.TryGetProperty("parts", out var parts) && parts.TryGetInt32(out var value)
                ? value
                : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>The CLI's JSON error, or its stderr, in plain words. Public so it can be tested without a process.</summary>
    public static (string Kind, string Message) ParseError(string stdout, string stderr)
    {
        try
        {
            using var doc = JsonDocument.Parse(stdout);
            if (doc.RootElement.TryGetProperty("error", out var error))
            {
                var kind = error.TryGetProperty("kind", out var k) ? k.GetString() ?? "Other" : "Other";
                var message = error.TryGetProperty("message", out var m) ? m.GetString() ?? string.Empty : string.Empty;
                if (!string.IsNullOrWhiteSpace(message)) return (kind, message);
            }
        }
        catch (JsonException) { /* not JSON: fall through to stderr */ }

        return ("Other", string.IsNullOrWhiteSpace(stderr)
            ? "The Telegram notifier failed. Check its configuration and try again."
            : stderr.Trim());
    }
}
