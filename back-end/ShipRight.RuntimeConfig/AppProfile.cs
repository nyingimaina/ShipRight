namespace ShipRight.RuntimeConfig;

/// <summary>
/// Resolves the runtime identity and isolation settings for a ShipRight installation.
///
/// Side-by-side installs (e.g. v3 and v4 simultaneously) are supported through a
/// "profile" name. An empty profile reproduces the legacy hardcoded behaviour
/// exactly; a non-empty profile suffixes every shared resource (data directory,
/// TCP port, named mutex, WebView2 profile, log directories) so two installations
/// never collide.
/// </summary>
public sealed record AppProfile
{
    public const int DefaultPort = 5200;
    public const int ProfiledDefaultPort = 5201;

    private const string DataDirectoryArgument = "--data-dir";
    private const string PortArgument = "--port";
    private const string ProfileArgument = "--profile";

    /// <summary>Empty for the legacy/backward-compatible profile, e.g. "4" otherwise.</summary>
    public string ProfileName { get; }

    /// <summary>TCP port the server binds and the desktop connects to.</summary>
    public int Port { get; }

    /// <summary>Absolute path to the user data directory (projects, builds, keys, backups).</summary>
    public string DataDirectory { get; }

    /// <summary>Absolute path to the per-user app home (WebView2 profile + desktop logs).</summary>
    public string AppHomeDirectory { get; }

    /// <summary>Named mutex guarding single-instance for the desktop process.</summary>
    public string MutexName { get; }

    /// <summary>WebView2 user-data folder, isolated per profile.</summary>
    public string WebView2Directory => Path.Combine(AppHomeDirectory, "WebView2");

    /// <summary>Desktop rolling log directory, isolated per profile.</summary>
    public string DesktopLogDirectory => Path.Combine(AppHomeDirectory, "logs");

    /// <summary>Human-readable label used for shortcut/install-dir naming (e.g. "" or "-4").</summary>
    public string Suffix => string.IsNullOrEmpty(ProfileName) ? string.Empty : "-" + ProfileName;

    private AppProfile(string profileName, int port, string dataDirectory, string appHomeDirectory)
    {
        ProfileName = profileName;
        Port = port;
        DataDirectory = dataDirectory;
        AppHomeDirectory = appHomeDirectory;
        MutexName = "ShipRight.Desktop" + Suffix;
    }

    /// <summary>
    /// Resolves the active profile from command-line args and environment values.
    /// Explicit <c>--data-dir</c> / <c>SHIPRIGHT_DATA_DIR</c> always win; otherwise the
    /// trail value is derived from the profile name (and the default legacy location).
    /// </summary>
    public static AppProfile Resolve(
        IReadOnlyList<string> args,
        string? profileEnv,
        string? portEnv,
        string? dataDirEnv,
        string userProfile,
        string localAppData)
    {
        var profileName = ReadArg(args, ProfileArgument) ?? profileEnv ?? string.Empty;

        var explicitPort = ReadArg(args, PortArgument) ?? portEnv;
        var explicitDataDir = ReadArg(args, DataDirectoryArgument) ?? dataDirEnv;

        var isValidProfile = IsValidProfileName(profileName);
        if (!isValidProfile)
            profileName = string.Empty;

        var port = ParsePort(explicitPort, IsProfiled(profileName) ? ProfiledDefaultPort : DefaultPort);
        var dataDirectory = ResolveDataDirectory(explicitDataDir, userProfile, profileName);
        var appHome = ResolveAppHome(localAppData, profileName);

        return new AppProfile(profileName, port, dataDirectory, appHome);
    }

    public static string? ReadArg(IReadOnlyList<string> args, string name)
    {
        foreach (var arg in args)
        {
            if (arg.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
                return arg[(name.Length + 1)..].Trim();
        }

        return null;
    }

    private static bool IsValidProfileName(string profileName)
        => !string.IsNullOrWhiteSpace(profileName)
           && profileName.All(c => char.IsLetterOrDigit(c) || c is '-' or '_');

    private static bool IsProfiled(string profileName) => !string.IsNullOrEmpty(profileName);

    private static int ParsePort(string? explicitPort, int fallback)
    {
        if (int.TryParse(explicitPort, out var port) && port is > 0 and <= 65535)
            return port;

        return fallback;
    }

    private static string ResolveDataDirectory(string? explicitDataDir, string userProfile, string profileName)
    {
        if (!string.IsNullOrWhiteSpace(explicitDataDir))
            return Path.GetFullPath(explicitDataDir);

        var baseDir = Path.Combine(userProfile, ".shipright");
        return IsProfiled(profileName) ? baseDir + "-" + profileName : baseDir;
    }

    private static string ResolveAppHome(string localAppData, string profileName)
    {
        var baseDir = Path.Combine(localAppData, "ShipRight");
        return IsProfiled(profileName) ? baseDir + "-" + profileName : baseDir;
    }
}
