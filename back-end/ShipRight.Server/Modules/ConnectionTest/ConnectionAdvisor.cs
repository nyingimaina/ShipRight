using ShipRight.Modules.JitDeploy;

namespace ShipRight.Modules.ConnectionTest;

/// <summary>
/// Explains what a server's answers mean, one checklist line each. A missing tool is only a warning when this user
/// can install it (the first deploy does that by itself); it is a failure when it cannot. Without evidence a line is
/// a warning, never a pass. Fixes are written for someone who has not used a server before, and any command is
/// meant to be copied and run by the user on the server.
/// </summary>
public static class ConnectionAdvisor
{
    private const string OptInSudo =
        "echo \"$USER ALL=(ALL) NOPASSWD:ALL\" | sudo tee \"/etc/sudoers.d/$USER-nopasswd\" >/dev/null && sudo chmod 440 \"/etc/sudoers.d/$USER-nopasswd\"";

    public static IReadOnlyList<CheckItem> Evaluate(ServerFacts f, string? workingDir, int minFreeMb = 3000)
    {
        var sudo = f.Get("sudo");
        var canSudo = sudo is "root" or "ok";
        var git = f.Get("git");
        var docker = f.Get("docker");
        var dockerUse = f.Get("dockeruse");
        var compose = f.Get("compose");
        var needInstall = git == "missing" || docker == "missing" || compose == "missing" || dockerUse is "needs-group" or "down";
        var pkg = f.Get("pkg");

        return
        [
            Sudo(sudo, canSudo, needInstall),
            DockerItem(docker, dockerUse, canSudo, pkg),
            ComposeItem(docker, compose, canSudo, pkg),
            GitItem(git, canSudo, pkg),
            Disk(f.Get("disk"), minFreeMb),
            Folder(f.Get("dir"), workingDir),
        ];
    }

    private static CheckItem Item(string id, CheckStatus s, string message, string? fix = null, string? command = null) =>
        new(id, CheckIds.Title(id), s, message, fix, command);

    private static CheckItem Unknown(string id, string what) =>
        Item(id, CheckStatus.Warn, $"Could not check {what}.", "Run the test again. If this keeps happening, make sure you can log in to the server with SSH.");

    private static string Install(string? pkg, string tools) => pkg switch
    {
        "apt" => $"sudo apt-get update && sudo apt-get install -y {tools}",
        "dnf" => $"sudo dnf install -y {tools}",
        _ => "",
    };

    private static CheckItem Sudo(string? sudo, bool canSudo, bool needInstall)
    {
        if (sudo is null) return Unknown(CheckIds.Sudo, "whether this user can install software");
        if (canSudo)
            return Item(CheckIds.Sudo, CheckStatus.Pass, sudo == "root" ? "You log in as the administrator, so installing software is allowed." : "This user can install software (passwordless sudo is available).");
        if (!needInstall)
            return Item(CheckIds.Sudo, CheckStatus.Pass, "Passwordless sudo is not needed right now: everything is already installed. Sudo would ask for a password, which is fine.");
        return Item(CheckIds.Sudo, CheckStatus.Fail,
            "Installing software needs sudo, and this user would have to type a password (or has no sudo at all). ShipRight cannot type a password for you.",
            "Either install the missing tools yourself with the commands shown on their lines, or give this user passwordless sudo with the command below. " +
            "Passwordless sudo gives this user full administrator rights on the server, so only do it on a server you are comfortable with.",
            OptInSudo);
    }

    private static CheckItem DockerItem(string? docker, string? use, bool canSudo, string? pkg)
    {
        const string id = CheckIds.Docker;
        if (docker is null) return Unknown(id, "Docker");
        if (docker == "missing")
        {
            if (canSudo) return Item(id, CheckStatus.Warn, "Docker is not installed yet. ShipRight installs it automatically on the first deploy.");
            var cmd = Install(pkg, "docker.io git");
            return Item(id, CheckStatus.Fail, "Docker is not installed, and this user cannot install it.",
                "Install Docker on the server yourself, then test again." + (cmd.Length == 0 ? " See docs.docker.com/engine/install for your system." : ""),
                cmd.Length == 0 ? null : cmd);
        }
        switch (use)
        {
            case "ok":
                return Item(id, CheckStatus.Pass, "Docker is installed and this user can use it.");
            case "needs-group":
                return Item(id, canSudo ? CheckStatus.Warn : CheckStatus.Fail,
                    "Docker is installed, but this user is not allowed to use it yet." + (canSudo ? " ShipRight fixes this automatically on the first deploy." : ""),
                    "This adds the user to the 'docker' group so it can run Docker without sudo. Members of that group can effectively act as administrator on this server, so only do it for a user you trust. " +
                    "You need to log in again (reconnect) for it to take effect.",
                    "sudo usermod -aG docker \"$USER\"");
            case "down":
                return Item(id, canSudo ? CheckStatus.Warn : CheckStatus.Fail,
                    "Docker is installed but not running." + (canSudo ? " ShipRight starts it automatically on the first deploy." : ""),
                    "Start it and make it start on every boot with the command below.",
                    "sudo systemctl enable --now docker");
            default:
                return Unknown(id, "whether Docker is running");
        }
    }

    private static CheckItem ComposeItem(string? docker, string? compose, bool canSudo, string? pkg)
    {
        const string id = CheckIds.Compose;
        if (docker == "missing") return Item(id, CheckStatus.Skipped, "Checked after Docker is installed.");
        return compose switch
        {
            "ok" => Item(id, CheckStatus.Pass, "Docker Compose is installed."),
            "missing" when canSudo => Item(id, CheckStatus.Warn, "Docker Compose is not installed yet. ShipRight installs it automatically on the first deploy."),
            "missing" => Item(id, CheckStatus.Fail, "Docker Compose is not installed, and this user cannot install it.",
                "Install the Docker Compose plugin on the server, then test again.",
                pkg switch { "apt" => "sudo apt-get install -y docker-compose-v2", "dnf" => "sudo dnf install -y docker-compose-plugin", _ => null }),
            _ => Unknown(id, "Docker Compose"),
        };
    }

    private static CheckItem GitItem(string? git, bool canSudo, string? pkg)
    {
        const string id = CheckIds.Git;
        return git switch
        {
            "ok" => Item(id, CheckStatus.Pass, "Git is installed."),
            "missing" when canSudo => Item(id, CheckStatus.Warn, "Git is not installed yet. ShipRight installs it automatically on the first deploy."),
            "missing" => Item(id, CheckStatus.Fail, "Git is not installed, and this user cannot install it.",
                "Install git on the server, then test again.", Install(pkg, "git") is { Length: > 0 } c ? c : null),
            _ => Unknown(id, "git"),
        };
    }

    private static CheckItem Disk(string? disk, int minFreeMb)
    {
        if (!int.TryParse(disk, out var mb)) return Unknown(CheckIds.Disk, "free disk space");
        if (mb >= minFreeMb) return Item(CheckIds.Disk, CheckStatus.Pass, $"{mb} MB free.");
        var fix = "Free some space on the server (for example remove old Docker images with 'docker image prune' if you do not need them).";
        if (mb < 500) return Item(CheckIds.Disk, CheckStatus.Fail, $"Only {mb} MB free: too little to build anything.", fix);
        return Item(CheckIds.Disk, CheckStatus.Warn, $"Only {mb} MB free. Deploys that build on the server refuse to start below {minFreeMb} MB.", fix);
    }

    private static CheckItem Folder(string? dir, string? workingDir)
    {
        const string id = CheckIds.Folder;
        if (string.IsNullOrWhiteSpace(workingDir) || dir == "unset")
            return Item(id, CheckStatus.Warn, "No deploy folder has been chosen for this server yet.", "Pick the folder on the server where the project should live, for example /home/ubuntu/shop.");
        switch (dir)
        {
            case "ok": return Item(id, CheckStatus.Pass, "The deploy folder exists and this user can write to it.");
            case "creatable": return Item(id, CheckStatus.Pass, "The deploy folder does not exist yet; it will be created.");
            case "readonly":
            case "blocked":
                var q = ShellQuote.Single(workingDir);
                return Item(id, CheckStatus.Fail, "This user cannot write to the deploy folder.",
                    "Create the folder and hand it to this user with the command below, or choose a folder inside the user's home directory.",
                    $"sudo mkdir -p {q} && sudo chown \"$USER\" {q}");
            default: return Unknown(id, "the deploy folder");
        }
    }
}
