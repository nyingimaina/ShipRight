using System.Text.Json;
using ShipRight.Modules.Resources.Models;
using ShipRight.Modules.Resources.Stores;
using ShipRight.Shared.CommandExecution;
using ShipRight.Shared.ProcessRunner;

namespace ShipRight.Modules.Resources;

public sealed record AwsProfileValidationResult(
    bool Ok,
    string? AccountId = null,
    string? Arn = null,
    string? ErrorCode = null,
    string? Message = null,
    string? Hint = null);

/// <summary>
/// Verifies AWS credentials actually work by running
/// `aws sts get-caller-identity`. Uses the same environment resolution as
/// <see cref="AwsEcrAuthProvider"/> so what gets tested is exactly what a push uses.
/// Failures are mapped to structured codes so the UI can suggest fixes.
/// </summary>
public sealed class AwsProfileValidator
{
    private static readonly TimeSpan StsTimeout = TimeSpan.FromSeconds(60);

    private readonly ICommandExecutor? _executor;
    private readonly IAwsProfileResourceStore? _profileStore;

    public AwsProfileValidator(
        IProcessRunner? runner, IAwsProfileResourceStore? profileStore = null, ICommandExecutor? executor = null)
    {
        _profileStore = profileStore;
        _executor = executor ?? (runner is null ? null : CommandExecutor.PassthroughLocal(runner));
    }

    /// <param name="profileId">Resolved from the store when given.</param>
    /// <param name="inlineProfile">Used when no profileId is supplied (e.g. unsaved form).</param>
    public async Task<AwsProfileValidationResult> ValidateAsync(
        Guid? profileId = null, AwsProfileResource? inlineProfile = null)
    {
        if (_executor is null)
            return Failure("aws-cli-missing", "No process runner is available to invoke the AWS CLI.");

        AwsProfileResource? profile;
        if (profileId is not null)
        {
            if (_profileStore is null)
                return Failure("aws-cli-missing", "No AWS profile store is available.");
            profile = await _profileStore.GetByIdAsync(profileId.Value);
            if (profile is null)
            {
                const string hint =
                    "The registry references an AWS profile that no longer exists. Create it again or unlink it from the registry.";
                return Failure("profile-not-found", $"AWS profile resource '{profileId}' not found.", hint);
            }
        }
        else
        {
            profile = inlineProfile;
        }

        if (profile is null)
            return Failure("profile-not-found", "No AWS profile was provided to validate.");

        if (profile.UsesExplicitKeys && string.IsNullOrEmpty(profile.AccessKeyId))
            return Failure("profile-not-found", "The AWS profile has no Access Key Id set.");

        var envOverride = AwsProfileEnvironment.Build(profile);

        ProcessResult result;
        try
        {
            result = await _executor.RunAsync(
                "aws", ["sts", "get-caller-identity", "--output", "json"],
                null, timeout: StsTimeout, envOverride: envOverride);
        }
        catch (global::System.ComponentModel.Win32Exception)
        {
            return Failure("aws-cli-missing", "The AWS CLI could not be started on the build machine.",
                "Install AWS CLI on the build machine — ShipRight can offer to do it here, or run \"sudo apt install awscli\" (WSL/Linux) / \"winget install Amazon.AWSCLI\" (Windows). Snap installs live in /snap/bin and need it on PATH.");
        }
        catch (TimeoutException)
        {
            return Failure("network", "Checking credentials timed out (60s).");
        }

        if (result.Success)
            return FromSuccessfulIdentity(result.StdOut);

        return FromFailureOutput(result.StdErr, envOverride, result.ExitCode);
    }

    private static AwsProfileValidationResult FromSuccessfulIdentity(string stdout)
    {
        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var account = doc.RootElement.TryGetProperty("Account", out var acc) ? acc.GetString() : null;
            var arn = doc.RootElement.TryGetProperty("Arn", out var arnEl) ? arnEl.GetString() : null;
            if (string.IsNullOrEmpty(arn))
                return Failure("empty-output",
                    "The AWS CLI returned an unexpected response (no identity Arn).");

            return new AwsProfileValidationResult(true, AccountId: account, Arn: arn);
        }
        catch (JsonException)
        {
            return Failure("empty-output", "The AWS CLI returned an unreadable response.",
                "Check that the build machine has AWS CLI v2 installed and reachable.");
        }
    }

    private static AwsProfileValidationResult FromFailureOutput(
        string stderr, IReadOnlyDictionary<string, string>? envOverride, int exitCode)
    {
        var text = stderr ?? string.Empty;
        var lower = text.ToLowerInvariant();

        // The CLI could not be invoked at all (missing from PATH / never executed).
        // Must be checked before any "not found"/"profile" substring matching below.
        if (exitCode == 127 || lower.Contains("no such file or directory") || lower.Contains("command not found"))
        {
            const string hint =
                "The AWS CLI is missing on the build machine. Use 'Install AWS CLI' to install it, " +
                "or run \"sudo apt install awscli\" — note snap installs live in /snap/bin which is only " +
                "on PATH in login shells.";
            return Failure("aws-cli-missing", MarkText(stderr, "The AWS CLI could not be run on the build machine."), hint);
        }

        if (lower.Contains("expiredtoken") || lower.Contains("token has expired"))
        {
            const string hint = "Regenerate the access key in IAM → Users → Security credentials → Create access key, then update this profile.";
            return Failure("expired-token",
                MarkText(stderr, "The AWS credentials are expired."), hint);
        }

        if (lower.Contains("accessdenied") || lower.Contains("is not authorized"))
        {
            return Failure("denied",
                MarkText(stderr, "The credentials are valid but have insufficient permissions."),
                "Add the 'ecr:GetAuthorizationToken' permission to the IAM user/role (or use a policy allowing ECR push).");
        }

        if (lower.Contains("invalidclienttokenid") || lower.Contains("signaturedoesnotmatch"))
        {
            const string hint =
                "The access key is invalid or no longer exists — create a fresh key in IAM (Users → Security credentials → Create access key) and update this profile.";
            return Failure("invalid-credentials",
                MarkText(stderr, "The AWS credentials were rejected."), hint);
        }

        // ProcessRunner redacts any stderr mentioning token/secret/password; a fully
        // redacted line means the CLI did run and rejected the request.
        if (text.Trim() == "[REDACTED]")
        {
            const string hint =
                "The rejection detail was hidden because it may contain a key. The most likely causes are an " +
                "expired or invalid access key — regenerate it in IAM (Users → Security credentials) and update this profile.";
            return Failure("invalid-credentials",
                "The AWS credentials were rejected (the original message was redacted as it may contain a secret).", hint);
        }

        if (lower.Contains("unable to locate credentials") || lower.Contains("no credentials")
            || lower.Contains("could not connect") || lower.Contains("connection timed out")
            || lower.Contains("network is unreachable"))
        {
            return Failure("network",
                MarkText(stderr, "No usable AWS credentials were found."),
                "If using a named profile, run 'aws configure --profile <name>' on the build machine, or paste explicit access keys here.");
        }

        var namedProfile = envOverride is not null && envOverride.TryGetValue("AWS_PROFILE", out var profile)
            ? profile
            : null;
        if (namedProfile is not null
            && (lower.Contains("not found") || lower.Contains("no such profile") || lower.Contains("profile")))
        {
            return Failure("profile-not-found",
                $"The named profile '{namedProfile}' was not found on the build machine.",
                $"Run 'aws configure --profile {namedProfile}' there, or switch this profile to explicit access keys.");
        }

        return Failure("network",
            MarkText(stderr, "Failed to reach AWS or resolve credentials."),
            "Check connectivity and that the credentials are present on the build machine.");
    }

    private static string MarkText(string? text, string summary)
    {
        var trimmed = text?.Trim() ?? string.Empty;
        return trimmed.Length > 0 ? $"{summary} AWS CLI said: {trimmed}" : summary;
    }

    private static AwsProfileValidationResult Failure(string code, string message, string? hint = null) =>
        new(false, ErrorCode: code, Message: message, Hint: hint);
}