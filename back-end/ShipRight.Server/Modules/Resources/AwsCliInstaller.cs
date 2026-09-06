using ShipRight.Shared.CommandExecution;
using ShipRight.Shared.ProcessRunner;

namespace ShipRight.Modules.Resources;

/// <summary>
/// Installs the AWS CLI on the build machine when it is entirely missing.
/// Orders: official v2 installer (curl+unzip) → apt → snap, all behind a
/// `sudo -n` gate so nothing hangs on a password prompt. When sudo is not
/// available the caller gets an exact command to run manually, and can instead
/// choose a no-sudo user-level install via pip. All commands go through the
/// <see cref="ICommandExecutor"/> so they run WSL-wrapped on Windows, natively
/// on Linux, or over SSH for remote build machines.
/// </summary>
public sealed class AwsCliInstaller
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(240);
    private static readonly TimeSpan SudoProbeTimeout = TimeSpan.FromSeconds(20);

    private const string OfficialV2Command =
        "curl -fsSL -o /tmp/awscliv2.zip https://awscli.amazonaws.com/awscli-exe-linux-x86_64.zip && " +
        "unzip -q /tmp/awscliv2.zip -d /tmp && sudo /tmp/aws/install";

    private readonly ICommandExecutor _executor;
    private readonly IWslToolLocator _locator;

    public AwsCliInstaller(ICommandExecutor executor, IWslToolLocator locator)
    {
        _executor = executor;
        _locator = locator;
    }

    public async Task<AwsCliInstallResult> RunAsync(AwsCliInstallRequest? request, CancellationToken ct = default)
    {
        if (await _locator.LocateAsync("aws", refresh: true, ct) is not null)
            return new(true, "AWS CLI is already installed on the build machine.");

        if (string.Equals(request?.Strategy, "pip", StringComparison.OrdinalIgnoreCase))
            return await InstallPipAsync(ct);

        return await InstallAutoAsync(ct);
    }

    private async Task<AwsCliInstallResult> InstallPipAsync(CancellationToken ct)
    {
        var result = await RunAsync("pip3", ["install", "--user", "awscli"], ct);
        if (!result.Success)
            return FailureResult("The no-sudo pip install failed.", result);

        return await AfterInstallCheckAsync("Installed via pip (user-level, no sudo needed).", ct);
    }

    private async Task<AwsCliInstallResult> InstallAutoAsync(CancellationToken ct)
    {
        var sudo = await RunAsync("sudo", ["-n", "true"], ct, SudoProbeTimeout);
        if (!sudo.Success)
            return new(false, "Installing the AWS CLI needs sudo, which is not available automatically.",
                RequiresManualInstall: true, Command: OfficialV2Command);

        if (await HasToolAsync("curl", ct) && await HasToolAsync("unzip", ct))
        {
            var download = await RunAsync("curl",
                ["-fsSL", "-o", "/tmp/awscliv2.zip", "https://awscli.amazonaws.com/awscli-exe-linux-x86_64.zip"], ct);
            if (!download.Success)
                return FailureResult("Downloading the AWS CLI failed.", download);

            var extract = await RunAsync("unzip", ["-q", "-o", "/tmp/awscliv2.zip", "-d", "/tmp"], ct);
            if (!extract.Success)
                return FailureResult("Extracting the AWS CLI failed.", extract);

            var install = await RunAsync("sudo", ["/tmp/aws/install"], ct);
            if (!install.Success)
                return FailureResult("Running the AWS CLI installer failed.", install);

            return await AfterInstallCheckAsync("Installed AWS CLI v2.", ct);
        }

        if (await HasToolAsync("apt-get", ct))
        {
            var update = await RunAsync("sudo", ["apt-get", "update"], ct);
            if (!update.Success)
                return FailureResult("apt update failed.", update);

            var install = await RunAsync("sudo", ["apt-get", "install", "-y", "awscli"], ct);
            if (!install.Success)
                return FailureResult("apt install awscli failed.", install);

            return await AfterInstallCheckAsync("Installed via apt.", ct);
        }

        if (await HasToolAsync("snap", ct))
        {
            var install = await RunAsync("sudo", ["snap", "install", "aws-cli", "--classic"], ct);
            if (!install.Success)
                return FailureResult("snap install aws-cli failed.", install);

            return await AfterInstallCheckAsync("Installed via snap.", ct);
        }

        return new(false, "No supported package manager (curl+unzip, apt or snap) was found on the build machine.",
            RequiresManualInstall: true, Command: OfficialV2Command);
    }

    private async Task<AwsCliInstallResult> AfterInstallCheckAsync(string successMessage, CancellationToken ct)
    {
        var path = await _locator.LocateAsync("aws", refresh: true, ct);
        return path is not null
            ? new(true, $"{successMessage} Found the AWS CLI at {path}.")
            : new(false, "The install finished but the AWS CLI still isn't reachable. Refresh PATH and try again.",
                RequiresManualInstall: true, Command: OfficialV2Command);
    }

    private static AwsCliInstallResult FailureResult(string message, ProcessResult result)
    {
        var tail = TailLines($"{result.StdOut}\n{result.StdErr}");
        return new(false, string.IsNullOrEmpty(tail) ? message : $"{message} {tail}", OutputTail: tail);
    }

    private async Task<bool> HasToolAsync(string tool, CancellationToken ct) =>
        (await RunAsync("bash", ["-lc", $"command -v {tool}"], ct)).Success;

    private Task<ProcessResult> RunAsync(string executable, string[] args, CancellationToken ct, TimeSpan? timeout = null)
        => _executor.RunAsync(executable, args, null, timeout: timeout ?? CommandTimeout, ct: ct);

    private static string TailLines(string text)
    {
        var lines = text.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).TakeLast(6);
        return string.Join(' ', lines);
    }
}