using ShipRight.Shared.ProcessRunner;

namespace ShipRight.Modules.Resources;

public interface IAwsCredentialsReader
{
    Task<AwsCredentialsSource> ReadAsync();
}

/// <summary>
/// Reads the AWS shared credentials/config files from the build machine.
/// On Windows the WSL home is tried first (builds run there), falling back to the
/// native %USERPROFILE%\.aws; on Linux the native ~/.aws is used. Only profile
/// metadata is returned — never the secret key values.
/// </summary>
public class AwsCredentialsReader : IAwsCredentialsReader
{
    private static readonly TimeSpan WslTimeout = TimeSpan.FromSeconds(15);

    private readonly IProcessRunner? _runner;
    private readonly string _nativeHome;
    private readonly bool _tryWsl;

    public AwsCredentialsReader(IProcessRunner? runner = null)
    {
        _runner = runner;
        _tryWsl = OperatingSystem.IsWindows();
        _nativeHome = OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : Environment.GetEnvironmentVariable("HOME") ?? string.Empty;
    }

    internal AwsCredentialsReader(string nativeHome, bool tryWsl, IProcessRunner? runner = null)
    {
        _nativeHome = nativeHome;
        _tryWsl = tryWsl;
        _runner = runner;
    }

    public async Task<AwsCredentialsSource> ReadAsync()
    {
        if (_tryWsl && _runner is not null)
        {
            var wslCreds = await ReadWslFileAsync("~/.aws/credentials");
            var wslConfig = await ReadWslFileAsync("~/.aws/config");
            if (wslCreds.Present || wslConfig.Present)
                return new AwsCredentialsSource(AwsCredentialsSourceKind.Wsl, true,
                    AwsCredentialsParser.Parse(wslCreds.Content, wslConfig.Content));
        }

        return ReadNative();
    }

    private async Task<(bool Present, string Content)> ReadWslFileAsync(string path)
    {
        var result = await _runner!.RunAsync(
            "wsl", ["-e", "sh", "-c", $"cat {path} 2>/dev/null"],
            null, timeout: WslTimeout);
        if (!result.Success)
            return (false, string.Empty);
        return (result.StdOut.Trim().Length > 0, result.StdOut);
    }

    private AwsCredentialsSource ReadNative()
    {
        var credentialsPath = Path.Combine(_nativeHome, ".aws", "credentials");
        var configPath = Path.Combine(_nativeHome, ".aws", "config");

        var credentials = ReadNativeIfExists(credentialsPath);
        var config = ReadNativeIfExists(configPath);

        if (credentials is null && config is null)
            return new AwsCredentialsSource(AwsCredentialsSourceKind.Native, false, []);

        return new AwsCredentialsSource(AwsCredentialsSourceKind.Native, true,
            AwsCredentialsParser.Parse(credentials, config));
    }

    private static string? ReadNativeIfExists(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}