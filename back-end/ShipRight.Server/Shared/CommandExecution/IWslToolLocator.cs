namespace ShipRight.Shared.CommandExecution;

/// <summary>
/// Locates the absolute path of a Linux tool so it can run in non-login
/// environments where /snap/bin and ~/.local/bin are not on PATH. Runs
/// `command -v` in a login shell (via bash -lc, WSL-wrapped on Windows), which
/// finds apt (/usr/bin/aws), snap (/snap/bin/aws) and pip (~/.local/bin/aws)
/// installs alike. Results are cached per tool.
/// </summary>
public interface IWslToolLocator
{
    /// <summary>Returns the absolute path of the tool, or null when it cannot be found.</summary>
    Task<string?> LocateAsync(string tool, bool refresh = false, CancellationToken ct = default);
}