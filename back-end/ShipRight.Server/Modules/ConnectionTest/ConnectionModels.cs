namespace ShipRight.Modules.ConnectionTest;

public enum CheckStatus { Pass, Warn, Fail, Skipped }

/// <summary>
/// One line of the connection checklist. <see cref="Message"/> says what was found in plain words,
/// <see cref="Fix"/> says what to do next, and <see cref="Command"/> is something to copy and run
/// on the server (never run by ShipRight itself).
/// </summary>
public sealed record CheckItem(string Id, string Title, CheckStatus Status, string Message, string? Fix = null, string? Command = null);

public sealed record ConnectionTestResult(IReadOnlyList<CheckItem> Items)
{
    /// <summary>Ready unless something failed. Warnings (like "will be installed on the first deploy") never block.</summary>
    public bool Ready => Items.All(i => i.Status != CheckStatus.Fail);

    public string Summary
    {
        get
        {
            var failed = Items.FirstOrDefault(i => i.Status == CheckStatus.Fail);
            if (failed is not null) return "Not ready: " + failed.Message;
            var warns = Items.Where(i => i.Status == CheckStatus.Warn).ToList();
            if (warns.Count == 0) return "The server is ready.";
            var text = "The server is ready. Things to know: " + string.Join(", ", warns.Select(w => w.Title.ToLowerInvariant())) + ".";
            if (warns.Any(w => w.Id is CheckIds.Docker or CheckIds.Compose or CheckIds.Git))
                text += " Missing tools are installed automatically on the first deploy.";
            return text;
        }
    }
}

public static class CheckIds
{
    public const string Reach = "reach", Login = "login", Sudo = "sudo", Docker = "docker", Compose = "compose", Git = "git", Disk = "disk", Folder = "folder";

    public static readonly IReadOnlyList<string> AfterLogin = [Sudo, Docker, Compose, Git, Disk, Folder];

    public static string Title(string id) => id switch
    {
        Reach => "Server is reachable",
        Login => "Login works",
        Sudo => "Can install software",
        Docker => "Docker",
        Compose => "Docker Compose",
        Git => "Git",
        Disk => "Free disk space",
        Folder => "Deploy folder",
        _ => id,
    };
}

/// <summary>The answers a server gave about itself: <c>key:value</c> lines, last one wins, keys ignore case.</summary>
public sealed class ServerFacts
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    internal void Set(string key, string value) => _values[key] = value;

    public string? Get(string key) => _values.GetValueOrDefault(key);
}
