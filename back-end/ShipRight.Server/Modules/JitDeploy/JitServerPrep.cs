namespace ShipRight.Modules.JitDeploy;

/// <summary>What a build-on-server deploy needs on the server.</summary>
public enum ServerTool { Git, Docker, Daemon, Compose, Buildx }

/// <summary>One thing to do to get the server ready. <see cref="Title"/> is shown to the user.</summary>
public sealed record PrepareStep(ServerTool Tool, string Title, string Command);

/// <summary>
/// Builds the commands that detect and, when needed, install what a build-on-server deploy needs: git, Docker
/// (running, and usable by the login user) and the Docker Compose plugin. Detecting changes nothing. Every install
/// step checks first, so repeating it is harmless, and installs only its own tool: a missing git never touches an
/// existing Docker. Nothing prunes, removes or names the deployer. Installing needs passwordless sudo, which every
/// step says in plain words when it is missing.
/// </summary>
public static class JitServerPrep
{
    private const string MissingPrefix = "missing:";

    private static string NeedSudo(string what) =>
        "sudo -n true 2>/dev/null || { echo '[prepare] Installing " + what + " needs passwordless sudo on this server. " +
        "Enable it for this user, or install it by hand, then deploy again.'; exit 1; }";

    private static string Marker(string name) => $": 'prepare:{name}'; ";

    /// <summary>Prints one <c>missing:&lt;tool&gt;</c> line per missing tool. Always exits 0. Changes nothing.</summary>
    public static string Detect() =>
        Marker("detect") +
        "command -v git >/dev/null 2>&1 || echo 'missing:git'; " +
        "if command -v docker >/dev/null 2>&1; then " +
        "docker info >/dev/null 2>&1 || echo 'missing:daemon'; " +
        "docker compose version >/dev/null 2>&1 || echo 'missing:compose'; " +
        "docker buildx version >/dev/null 2>&1 || echo 'missing:buildx'; " +
        "else echo 'missing:docker'; fi; true";

    public static IReadOnlyCollection<ServerTool> ParseMissing(IEnumerable<string> lines)
    {
        var found = new HashSet<ServerTool>();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (!line.StartsWith(MissingPrefix, StringComparison.Ordinal)) continue;
            if (Enum.TryParse<ServerTool>(line[MissingPrefix.Length..], true, out var tool) && Enum.IsDefined(tool))
                found.Add(tool);
        }
        return found;
    }

    /// <summary>The steps for what is missing, in the order they must run. No Docker means it also has to be started and given Compose.</summary>
    public static IReadOnlyList<PrepareStep> Plan(IEnumerable<ServerTool> missing)
    {
        var m = missing.ToHashSet();
        var steps = new List<PrepareStep>();
        if (m.Contains(ServerTool.Git)) steps.Add(new(ServerTool.Git, "Installing git", InstallGit()));
        if (m.Contains(ServerTool.Docker)) steps.Add(new(ServerTool.Docker, "Installing Docker", InstallDocker()));
        if (m.Contains(ServerTool.Docker) || m.Contains(ServerTool.Daemon))
            steps.Add(new(ServerTool.Daemon, "Starting Docker and allowing this user to use it", StartDocker()));
        if (m.Contains(ServerTool.Docker) || m.Contains(ServerTool.Compose))
            steps.Add(new(ServerTool.Compose, "Installing the Docker Compose plugin", InstallCompose()));
        if (m.Contains(ServerTool.Docker) || m.Contains(ServerTool.Buildx))
            steps.Add(new(ServerTool.Buildx, "Installing Docker BuildKit (buildx)", InstallBuildx()));
        return steps;
    }

    public static string Describe(ServerTool tool) => tool switch
    {
        ServerTool.Git => "git",
        ServerTool.Docker => "Docker",
        ServerTool.Daemon => "a running Docker this user can use",
        ServerTool.Compose => "Docker Compose",
        ServerTool.Buildx => "Docker BuildKit (buildx)",
        _ => tool.ToString(),
    };

    private static string InstallGit() =>
        Marker("git") +
        "if command -v git >/dev/null 2>&1; then echo 'git is already installed.'; else " + NeedSudo("git") + "; " +
        "if command -v dnf >/dev/null 2>&1; then sudo -n dnf install -y git; " +
        "elif command -v apt-get >/dev/null 2>&1; then sudo -n apt-get update -y && sudo -n apt-get install -y git; " +
        "else echo 'Unsupported OS: need dnf or apt-get.'; exit 1; fi; fi";

    private static string InstallDocker() =>
        Marker("docker") +
        "if command -v docker >/dev/null 2>&1; then echo 'Docker is already installed.'; else " + NeedSudo("Docker") + "; " +
        "if command -v dnf >/dev/null 2>&1; then sudo -n dnf install -y docker; " +
        "elif command -v apt-get >/dev/null 2>&1; then sudo -n apt-get update -y && sudo -n apt-get install -y docker.io; " +
        "else echo 'Unsupported OS: need dnf or apt-get.'; exit 1; fi; fi";

    private static string StartDocker() =>
        Marker("daemon") +
        "if docker info >/dev/null 2>&1; then echo 'Docker is already running and usable.'; else " + NeedSudo("and starting Docker") + "; " +
        "sudo -n systemctl enable --now docker && sudo -n usermod -aG docker \"$USER\" && sleep 2 && " +
        "echo 'Docker is started. This user can use it from the next connection.'; fi";

    /// <summary>
    /// BuildKit lets a Dockerfile use features like RUN --mount. Docker needs the buildx plugin for it. The plugin is added next to
    /// Docker's other plugins, only when missing; nothing else on the server is touched.
    /// </summary>
    private static string InstallBuildx() =>
        Marker("buildx") +
        "if docker buildx version >/dev/null 2>&1; then echo 'Docker BuildKit (buildx) is already installed.'; else " + NeedSudo("Docker BuildKit (buildx)") + "; " +
        "if ! command -v curl >/dev/null 2>&1; then " +
        "if command -v dnf >/dev/null 2>&1; then sudo -n dnf install -y curl; else sudo -n apt-get install -y curl; fi; fi; " +
        "case \"$(uname -m)\" in x86_64) a=amd64;; aarch64|arm64) a=arm64;; armv7l) a=arm-v7;; *) echo 'This kind of computer is not supported for the BuildKit plugin.'; exit 1;; esac; " +
        "ver=$(curl -fsSL https://api.github.com/repos/docker/buildx/releases/latest | grep -m1 '\"tag_name\"' | cut -d '\"' -f4) && [ -n \"$ver\" ] && " +
        "sudo -n mkdir -p /usr/local/lib/docker/cli-plugins && " +
        "sudo -n curl -fsSL \"https://github.com/docker/buildx/releases/download/$ver/buildx-$ver.linux-$a\" -o /usr/local/lib/docker/cli-plugins/docker-buildx && " +
        "sudo -n chmod +x /usr/local/lib/docker/cli-plugins/docker-buildx && docker buildx version; fi";

    private static string InstallCompose() =>
        Marker("compose") +
        "if docker compose version >/dev/null 2>&1; then echo 'Docker Compose is already installed.'; else " + NeedSudo("the Docker Compose plugin") + "; " +
        "if ! command -v curl >/dev/null 2>&1; then " +
        "if command -v dnf >/dev/null 2>&1; then sudo -n dnf install -y curl; else sudo -n apt-get install -y curl; fi; fi; " +
        "sudo -n mkdir -p /usr/local/lib/docker/cli-plugins && " +
        "sudo -n curl -fsSL \"https://github.com/docker/compose/releases/latest/download/docker-compose-linux-$(uname -m)\" " +
        "-o /usr/local/lib/docker/cli-plugins/docker-compose && " +
        "sudo -n chmod +x /usr/local/lib/docker/cli-plugins/docker-compose && " +
        "docker compose version; fi";
}
