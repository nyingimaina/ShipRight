using System.Text.RegularExpressions;
using ShipRight.Shared.ProcessRunner;

namespace ShipRight.Modules.System.SystemCheck;

public enum CheckStatus { Ok, Warning, Problem, Skipped }

public record SystemCheckItem(
    string Id, string Title, CheckStatus Status, string Detail,
    string? FixId = null,          // set when ShipRight can repair it by itself
    string? FixLabel = null,
    string? ManualSteps = null);   // set when the person has to do it (needs admin rights or a restart)

public record SystemCheckReport(IReadOnlyList<SystemCheckItem> Items, bool Healthy);

public record FixResult(bool Succeeded, string Message, string Output, SystemCheckReport Report);

/// <summary>
/// Looks at everything ShipRight needs on this computer - WSL, a Linux distro, Docker (running, usable, with
/// compose and buildx), git, and free disk space - and says in plain words what is wrong. Anything that can be
/// repaired from inside the Linux distro can be fixed in one click; steps that need administrator rights or a
/// restart of Windows (installing WSL itself) are explained instead of attempted. The check only reads; a fix
/// only runs for a problem that is present right now, from a fixed list, as root inside the chosen distro.
/// </summary>
public class SystemCheckService
{
    private const long WarnBelowBytes = 10L * 1024 * 1024 * 1024;
    private const long ProblemBelowBytes = 3L * 1024 * 1024 * 1024;
    private static readonly Regex SafeUserName = new("^[a-z_][a-z0-9_-]{0,31}$", RegexOptions.Compiled);

    private readonly IProcessRunner _runner;
    private readonly Func<long?> _freeBytes;

    public SystemCheckService(IProcessRunner runner, Func<long?>? freeBytes = null)
    {
        _runner = runner;
        _freeBytes = freeBytes ?? SystemDriveFreeBytes;
    }

    // ── fixes ─────────────────────────────────────────────────────────

    private sealed record Fix(string Label, string Explain);

    private static readonly Dictionary<string, Fix> Fixes = new()
    {
        ["install-docker"] = new("Install Docker", "Installs Docker inside your Linux distro using Docker's official install script. This downloads a few hundred MB and can take several minutes."),
        ["start-docker"] = new("Start Docker", "Starts the Docker service inside your Linux distro."),
        ["add-docker-group"] = new("Allow my user to use Docker", "Adds your Linux user to the docker group so builds do not need extra permissions."),
        ["install-docker-plugins"] = new("Install the missing Docker parts", "Installs Docker's compose and buildx add-ons."),
        ["install-git"] = new("Install git", "Installs git inside your Linux distro."),
    };

    public async Task<FixResult> FixAsync(string fixId, CancellationToken ct)
    {
        if (!Fixes.ContainsKey(fixId))
            return new(false, "That is not a repair ShipRight knows how to do.", "", await CheckAsync(ct));

        var before = await CheckAsync(ct);
        if (!before.Items.Any(i => i.FixId == fixId && i.Status is CheckStatus.Problem or CheckStatus.Warning))
            return new(false, "There is nothing to fix here - everything this repair covers is already working.", "", before);

        var distro = (await FindDistroAsync(ct)).Name;
        if (distro is null)
            return new(false, "No Linux distro was found to repair.", "", before);

        string script;
        switch (fixId)
        {
            case "install-docker": script = "curl -fsSL https://get.docker.com | sh"; break;
            case "start-docker": script = "service docker start || systemctl start docker"; break;
            case "install-docker-plugins": script = "apt-get update && apt-get install -y docker-compose-plugin docker-buildx-plugin"; break;
            case "install-git": script = "apt-get update && apt-get install -y git"; break;
            default: // add-docker-group
                var who = (await Wsl(distro, ["whoami"], ct)).StdOut.Trim();
                if (!SafeUserName.IsMatch(who))
                    return new(false, $"ShipRight could not safely work out your Linux user name (got '{who}').", "", before);
                script = $"usermod -aG docker {who}";
                break;
        }

        var run = await _runner.RunAsync("wsl", ["-d", distro, "-u", "root", "sh", "-c", script], null,
            timeout: TimeSpan.FromMinutes(fixId == "install-docker" ? 20 : 5), ct: ct);
        var output = Tail((run.StdOut + "\n" + run.StdErr).Trim());
        var after = await CheckAsync(ct);

        return run.Success
            ? new(true, $"{Fixes[fixId].Label}: done.", output, after)
            : new(false, $"{Fixes[fixId].Label} did not work (exit code {run.ExitCode}). The last lines of what happened are below.", output, after);
    }

    // ── checks ────────────────────────────────────────────────────────

    public async Task<SystemCheckReport> CheckAsync(CancellationToken ct)
    {
        var items = new List<SystemCheckItem>();
        const string skipWsl = "Checked once WSL is installed.";

        var wslOk = await WslInstalledAsync(ct);
        items.Add(wslOk
            ? Ok("wsl", "WSL (Windows Subsystem for Linux)", "WSL is installed.")
            : new("wsl", "WSL (Windows Subsystem for Linux)", CheckStatus.Problem,
                "ShipRight builds in Linux, which Windows provides through WSL. It is not installed on this computer.",
                ManualSteps: "Open the Start menu, type PowerShell, right-click it and choose \"Run as administrator\". Run:  wsl --install --no-distribution  then restart Windows and come back here."));

        if (!wslOk)
        {
            foreach (var (id, title) in new[] { ("distro", "A Linux distro"), ("wsl2", "WSL version 2"), ("docker", "Docker installed"), ("daemon", "Docker running and usable"), ("compose", "Docker Compose"), ("buildx", "Docker Buildx"), ("git", "git") })
                items.Add(Skipped(id, title, skipWsl));
            items.Add(Disk());
            return Report(items);
        }

        var found = await FindDistroAsync(ct);
        if (found.Name is null)
        {
            items.Add(new("distro", "A Linux distro", CheckStatus.Problem,
                "WSL is installed but there is no Linux inside it yet (Docker Desktop's own internal distros do not count).",
                ManualSteps: "Open PowerShell as administrator and run:  wsl --install -d Ubuntu  then follow the prompts to choose a user name and password."));
            foreach (var (id, title) in new[] { ("wsl2", "WSL version 2"), ("docker", "Docker installed"), ("daemon", "Docker running and usable"), ("compose", "Docker Compose"), ("buildx", "Docker Buildx"), ("git", "git") })
                items.Add(Skipped(id, title, "Checked once a Linux distro is installed."));
            items.Add(Disk());
            return Report(items);
        }

        var d = found.Name;
        items.Add(Ok("distro", "A Linux distro", $"Using {d}."));
        items.Add(found.Version == 1
            ? new("wsl2", "WSL version 2", CheckStatus.Warning,
                $"{d} runs on WSL 1, which is slow and cannot run Docker properly.",
                ManualSteps: $"In PowerShell run:  wsl --set-version {d} 2")
            : Ok("wsl2", "WSL version 2", "Your distro runs on WSL 2."));

        var dockerInstalled = (await Wsl(d, ["sh", "-c", "command -v docker"], ct)).Success;
        if (!dockerInstalled)
        {
            items.Add(Problem("docker", "Docker installed", "Docker is not installed inside your Linux distro.", "install-docker"));
            foreach (var (id, title) in new[] { ("daemon", "Docker running and usable"), ("compose", "Docker Compose"), ("buildx", "Docker Buildx") })
                items.Add(Skipped(id, title, "Checked once Docker is installed."));
        }
        else
        {
            items.Add(Ok("docker", "Docker installed", "Docker is installed."));

            var info = await Wsl(d, ["docker", "info", "--format", "{{.ServerVersion}}"], ct);
            if (info.Success)
                items.Add(Ok("daemon", "Docker running and usable", $"Docker {info.StdOut.Trim()} is running."));
            else if (info.StdErr.Contains("permission denied", StringComparison.OrdinalIgnoreCase))
                items.Add(Problem("daemon", "Docker running and usable", "Docker is running, but your user is not allowed to use it.", "add-docker-group"));
            else
                items.Add(Problem("daemon", "Docker running and usable", "Docker is installed but not running.", "start-docker"));

            items.Add((await Wsl(d, ["docker", "compose", "version"], ct)).Success
                ? Ok("compose", "Docker Compose", "Docker Compose is available.")
                : Problem("compose", "Docker Compose", "Docker Compose is missing. It starts your app on a server.", "install-docker-plugins"));
            items.Add((await Wsl(d, ["docker", "buildx", "version"], ct)).Success
                ? Ok("buildx", "Docker Buildx", "Docker Buildx is available.")
                : Problem("buildx", "Docker Buildx", "Docker Buildx is missing. It makes builds faster and cached.", "install-docker-plugins"));
        }

        items.Add((await Wsl(d, ["git", "--version"], ct)).Success
            ? Ok("git", "git", "git is available in Linux.")
            : Problem("git", "git", "git is not installed in your Linux distro. ShipRight uses it to build from a clean copy of your code.", "install-git"));

        items.Add(Disk());
        return Report(items);
    }

    // ── helpers ───────────────────────────────────────────────────────

    private static SystemCheckReport Report(List<SystemCheckItem> items) =>
        new(items, items.All(i => i.Status is CheckStatus.Ok or CheckStatus.Skipped));

    private static SystemCheckItem Ok(string id, string title, string detail) => new(id, title, CheckStatus.Ok, detail);

    private static SystemCheckItem Skipped(string id, string title, string detail) => new(id, title, CheckStatus.Skipped, detail);

    private static SystemCheckItem Problem(string id, string title, string detail, string fixId) =>
        new(id, title, CheckStatus.Problem, detail + " " + Fixes[fixId].Explain, fixId, Fixes[fixId].Label);

    private SystemCheckItem Disk()
    {
        var free = _freeBytes();
        if (free is null) return Skipped("disk", "Free space on your Windows drive", "Could not read the free space.");
        var gb = free.Value / (1024.0 * 1024 * 1024);
        var text = $"{gb:0.#} GB free on your Windows drive.";
        const string steps = "Open Utilities in ShipRight and use \"Clean up and shrink my Windows disk\". Builds, images and WSL can fill a drive quickly.";
        if (free < ProblemBelowBytes)
            return new("disk", "Free space on your Windows drive", CheckStatus.Problem, text + " That is dangerously low - builds will fail.", ManualSteps: steps);
        if (free < WarnBelowBytes)
            return new("disk", "Free space on your Windows drive", CheckStatus.Warning, text + " That is getting low.", ManualSteps: steps);
        return Ok("disk", "Free space on your Windows drive", text);
    }

    private async Task<bool> WslInstalledAsync(CancellationToken ct)
    {
        try { return (await _runner.RunAsync("wsl", ["--status"], null, timeout: TimeSpan.FromSeconds(20), ct: ct)).Success; }
        catch (Exception) when (!ct.IsCancellationRequested) { return false; }
    }

    private async Task<ProcessResult> Wsl(string distro, string[] command, CancellationToken ct)
    {
        try { return await _runner.RunAsync("wsl", ["-d", distro, .. command], null, timeout: TimeSpan.FromSeconds(60), ct: ct); }
        catch (Exception) when (!ct.IsCancellationRequested) { return new ProcessResult(-1, "", "could not run", TimeSpan.Zero); }
    }

    private sealed record Distro(string? Name, int? Version);

    /// <summary>The default Linux distro if it is usable, otherwise the first usable one. wsl prints UTF-16, so NULs are dropped.</summary>
    private async Task<Distro> FindDistroAsync(CancellationToken ct)
    {
        ProcessResult names, verbose;
        try
        {
            names = await _runner.RunAsync("wsl", ["-l", "-q"], null, timeout: TimeSpan.FromSeconds(20), ct: ct);
            verbose = await _runner.RunAsync("wsl", ["-l", "-v"], null, timeout: TimeSpan.FromSeconds(20), ct: ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested) { return new(null, null); }

        var usable = Clean(names.StdOut).Where(IsUsable).ToList();
        if (usable.Count == 0) return new(null, null);

        string? name = null;
        int? version = null;
        foreach (var line in Clean(verbose.StdOut))
        {
            var isDefault = line.TrimStart().StartsWith('*');
            var parts = line.Replace("*", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3 || !int.TryParse(parts[^1], out var v)) continue;
            if (!usable.Contains(parts[0], StringComparer.OrdinalIgnoreCase)) continue;
            if (isDefault || name is null) { name = parts[0]; version = v; }
            if (isDefault) break;
        }
        return name is null ? new(usable[0], null) : new(name, version);
    }

    private static bool IsUsable(string name) => !name.StartsWith("docker-desktop", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> Clean(string text) =>
        text.Replace("\0", "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.Length > 0);

    private static string Tail(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.TrimEntries);
        return string.Join("\n", lines.Length <= 12 ? lines : lines[^12..]);
    }

    private static long? SystemDriveFreeBytes()
    {
        try { return new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)!).AvailableFreeSpace; }
        catch { return null; }
    }
}
