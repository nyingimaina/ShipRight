using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ShipRight.Modules.Git;

public sealed record GitRepoInfo(
    string Provider, string Owner, string Name, string DefaultBranch, bool Private, string WebUrl, long? RepoId);

public sealed record GitBranch(string Name, string Sha, bool IsDefault);

public sealed record GitTreeEntry(string Path, string Type, long? Size);

public interface IGitProvider
{
    string Id { get; }
    bool CanHandle(GitUrlInfo url);
    Task<GitRepoInfo> GetRepoAsync(GitUrlInfo url, string? token, CancellationToken ct);
    Task<IReadOnlyList<GitBranch>> GetBranchesAsync(GitUrlInfo url, string? token, CancellationToken ct);
    Task<IReadOnlyList<GitTreeEntry>> GetTreeAsync(GitUrlInfo url, string reference, string? token, CancellationToken ct);
    Task<string?> GetFileAsync(GitUrlInfo url, string reference, string path, string? token, CancellationToken ct);
}

public sealed class GitProviderException(string message, HttpStatusCode? status = null) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
}

/// <summary>
/// Read-only GitHub REST access. It never writes, never stores the token and reports failures in plain words.
/// </summary>
public sealed class GitHubProvider(HttpClient http) : IGitProvider
{
    public string Id => "github";

    public bool CanHandle(GitUrlInfo url) => url.Provider == "github";

    public async Task<GitRepoInfo> GetRepoAsync(GitUrlInfo url, string? token, CancellationToken ct)
    {
        using var doc = await GetJsonAsync($"repos/{Escape(url.Owner)}/{Escape(url.Name)}", token, ct);
        var root = doc.RootElement;

        return new GitRepoInfo(
            "github",
            url.Owner,
            url.Name,
            root.TryGetProperty("default_branch", out var branch) ? branch.GetString() ?? "main" : "main",
            root.TryGetProperty("private", out var isPrivate) && isPrivate.GetBoolean(),
            root.TryGetProperty("html_url", out var web) ? web.GetString() ?? url.HttpsUrl : url.HttpsUrl,
            root.TryGetProperty("id", out var id) && id.TryGetInt64(out var number) ? number : null);
    }

    public async Task<IReadOnlyList<GitBranch>> GetBranchesAsync(GitUrlInfo url, string? token, CancellationToken ct)
    {
        using var doc = await GetJsonAsync($"repos/{Escape(url.Owner)}/{Escape(url.Name)}/branches?per_page=100", token, ct);
        var branches = new List<GitBranch>();

        if (doc.RootElement.ValueKind == JsonValueKind.Array)
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var name = item.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
                if (name.Length == 0) continue;
                var sha = item.TryGetProperty("commit", out var commit) && commit.TryGetProperty("sha", out var s)
                    ? s.GetString() ?? string.Empty
                    : string.Empty;
                branches.Add(new GitBranch(name, sha, false));
            }

        return branches;
    }

    public async Task<IReadOnlyList<GitTreeEntry>> GetTreeAsync(
        GitUrlInfo url, string reference, string? token, CancellationToken ct)
    {
        using var doc = await GetJsonAsync(
            $"repos/{Escape(url.Owner)}/{Escape(url.Name)}/git/trees/{Escape(reference)}?recursive=1", token, ct);

        var entries = new List<GitTreeEntry>();
        if (doc.RootElement.TryGetProperty("tree", out var tree) && tree.ValueKind == JsonValueKind.Array)
            foreach (var item in tree.EnumerateArray())
            {
                var path = item.TryGetProperty("path", out var p) ? p.GetString() ?? string.Empty : string.Empty;
                if (path.Length == 0) continue;
                var type = item.TryGetProperty("type", out var t) ? t.GetString() ?? string.Empty : string.Empty;
                long? size = item.TryGetProperty("size", out var s) && s.TryGetInt64(out var value) ? value : null;
                entries.Add(new GitTreeEntry(path, type, size));
            }

        return entries;
    }

    public async Task<string?> GetFileAsync(
        GitUrlInfo url, string reference, string path, string? token, CancellationToken ct)
    {
        using var doc = await GetJsonAsync(
            $"repos/{Escape(url.Owner)}/{Escape(url.Name)}/contents/{EscapePath(path)}?ref={Escape(reference)}", token, ct);

        if (!doc.RootElement.TryGetProperty("content", out var content)) return null;
        var encoded = (content.GetString() ?? string.Empty).Replace("\n", string.Empty);
        if (encoded.Length == 0) return null;
        return Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
    }

    private async Task<JsonDocument> GetJsonAsync(string relative, string? token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, relative);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.UserAgent.ParseAdd("ShipRight");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (!string.IsNullOrWhiteSpace(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());

        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var message = response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "GitHub refused the sign-in. Check the token and try again.",
                HttpStatusCode.Forbidden when IsRateLimited(response) =>
                    "GitHub's rate limit was reached. Add a token or try again later.",
                HttpStatusCode.Forbidden => "GitHub refused access. Check the token's permissions.",
                HttpStatusCode.NotFound => "That repository was not found, or the token cannot see it.",
                _ => $"GitHub answered {(int)response.StatusCode}. Try again in a moment.",
            };
            throw new GitProviderException(message, response.StatusCode);
        }

        var text = await response.Content.ReadAsStringAsync(ct);
        return JsonDocument.Parse(text);
    }

    private static bool IsRateLimited(HttpResponseMessage response) =>
        response.Headers.TryGetValues("X-RateLimit-Remaining", out var values) &&
        values.FirstOrDefault() == "0";

    private static string Escape(string value) => Uri.EscapeDataString(value);

    private static string EscapePath(string path) =>
        string.Join('/', path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
}
