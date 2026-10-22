using ShipRight.Modules.Projects;

namespace ShipRight.Modules.RemoteHost;

/// <summary>
/// Keeps the compose folder's git state predictable. Before each pull: local-only noise (secrets, home-folder dotfiles)
/// is ignored through .git/info/exclude (so nothing is added to the repo and pulls can't conflict on it), any
/// remaining local edits are committed locally, then the pull rebases onto the remote. A conflict aborts the rebase
/// and leaves the folder as it was. When the repo is the home folder itself nothing is auto-committed.
/// </summary>
public static class RemoteGitHygiene
{
    private static readonly string[] IgnoredHere =
    [
        ".env", ".env.*", ".shipright/", ".aws/", ".ssh/", ".docker/", ".cache/", ".config/", ".local/", ".npm/",
        ".gnupg/", "snap/", ".bashrc", ".bash_logout", ".bash_history", ".profile", ".viminfo", ".lesshst",
        ".sudo_as_admin_successful", ".wget-hsts",
    ];

    /// <summary>Shell fragment; run it inside the repo folder. Exits non-zero if the pull could not be applied.</summary>
    public static string BuildSafePullFragment()
    {
        var rules = string.Join(" ", IgnoredHere.Select(r => $"'{r}'"));
        return "mkdir -p .git/info && { grep -q 'shipright-managed' .git/info/exclude 2>/dev/null || " +
               $"printf '%s\\n' '# shipright-managed' {rules} >> .git/info/exclude; }}; " +
               "if [ \"$(git rev-parse --show-toplevel)\" = \"$HOME\" ]; then " +
               "echo '[git] This repo is your home folder: not auto-committing. Use a sub-folder as the working directory.'; " +
               "git pull --ff-only; " +
               "else git add -A && " +
               "{ git diff --cached --quiet || git -c user.name=ShipRight -c user.email=shipright@localhost " +
               "commit -q -m 'ShipRight: keep local changes before pull'; } && " +
               "{ git -c user.name=ShipRight -c user.email=shipright@localhost pull --rebase || { git rebase --abort 2>/dev/null; " +
               "echo '[git] Pull conflicts with local changes on this server; the folder was left unchanged.'; exit 1; }; }; fi";
    }

    /// <summary>Same as <see cref="RemoteCheckpoint.BuildSyncRepoCommand"/> but updates an existing clone with the safe pull.</summary>
    public static string BuildSafeSyncRepoCommand(ServerConfig server)
    {
        var dir = server.RemoteWorkingDir.TrimEnd('/');
        var url = server.ComposeRepoUrl;
        return $"mkdir -p '{dir}' && cd '{dir}' && " +
               "if [ -d .git ]; then " +
               $"(git remote set-url origin '{url}' 2>/dev/null || git remote add origin '{url}') && {{ {BuildSafePullFragment()}; }}; " +
               "else " +
               $"git init -q && (git remote add origin '{url}' 2>/dev/null || git remote set-url origin '{url}') && " +
               "git fetch origin && " +
               "BR=$(git remote show origin | sed -n 's/.*HEAD branch: //p') && " +
               "[ -n \"$BR\" ] && [ \"$BR\" != '(unknown)' ] || { echo 'Remote has no default branch (empty repo?).'; exit 1; }; " +
               "git checkout -f -B \"$BR\" \"origin/$BR\"; fi";
    }
}
