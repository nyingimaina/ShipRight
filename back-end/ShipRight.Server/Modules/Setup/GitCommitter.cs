using System.Text.RegularExpressions;
using ShipRight.Shared.ProcessRunner;

namespace ShipRight.Modules.Setup;

public record CommitResult(bool Success, string? Error);

/// <summary>
/// Commits an explicit list of files. Never stages everything, and refuses files that look like secrets,
/// because the person clicking "commit for me" may not know what a .env file is.
/// </summary>
public sealed class GitCommitter
{
    private static readonly Regex SecretName = new(
        @"^(\.env(\..+)?|id_(rsa|dsa|ecdsa|ed25519)|.+\.(pem|pfx|p12|key))$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex NotSecretSuffix = new(
        @"^\.env\.(example|sample|template|dist|defaults)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly IProcessRunner _runner;

    public GitCommitter(IProcessRunner runner) => _runner = runner;

    public async Task<CommitResult> CommitAsync(
        string repoPath, string message, IReadOnlyCollection<string> relativePaths, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(message)) return new CommitResult(false, "A commit needs a short description.");
        if (relativePaths.Count == 0) return new CommitResult(false, "There is nothing to commit.");

        var outside = relativePaths.Where(EscapesRepo).ToList();
        if (outside.Count > 0)
            return new CommitResult(false, $"These paths are outside the project and were refused: {string.Join(", ", outside)}");

        var secrets = relativePaths.Where(LooksSecret).ToList();
        if (secrets.Count > 0)
            return new CommitResult(false,
                $"Refusing to commit {string.Join(", ", secrets)}: it looks like a secret (password, key or certificate). " +
                "Add it to .gitignore instead.");

        var paths = relativePaths.ToArray();
        var add = await _runner.RunAsync("git", ["-C", repoPath, "add", "--", .. paths], null, ct: ct);
        if (!add.Success) return new CommitResult(false, Describe(add));

        var commit = await _runner.RunAsync("git", ["-C", repoPath, "commit", "-m", message, "--", .. paths], null, ct: ct);
        return commit.Success ? new CommitResult(true, null) : new CommitResult(false, Describe(commit));
    }

    private static bool EscapesRepo(string path) =>
        Path.IsPathRooted(path) || path.Replace('\\', '/').Split('/').Contains("..");

    private static bool LooksSecret(string path)
    {
        var name = path.Replace('\\', '/').Split('/').Last();
        return SecretName.IsMatch(name) && !NotSecretSuffix.IsMatch(name);
    }

    private static string Describe(ProcessResult r) =>
        (string.IsNullOrWhiteSpace(r.StdErr) ? r.StdOut : r.StdErr).Trim();
}
