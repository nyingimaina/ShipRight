using System.Text.RegularExpressions;

namespace ShipRight.Modules.Identity;

// One table decides the least role needed for every API call. Admin does everything; Operator can build and
// deploy but not change settings or servers; Viewer only reads. First matching rule wins; a call that matches
// nothing needs Operator when it writes and Viewer when it reads, so a new route is never open by accident.
public static class RoleAccessRules
{
    private sealed record Rule(string? Method, Regex Path, string Role);

    private static Rule R(string? method, string pattern, string role) =>
        new(method, new Regex("^" + pattern + "$", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant), role);

    private static readonly Rule[] Rules =
    [
        // Team management and the host machine: Admin only, reads included.
        R(null, @"/api/team(/.*)?", AppRoles.Admin),
        R(null, @"/api/system/(shutdown|wsl-disk(/.*)?)", AppRoles.Admin),
        R(null, @"/api/fs(/.*)?", AppRoles.Admin),
        R(null, @"/api/setup(/.*)?", AppRoles.Admin),

        // Sign-in plumbing for an already signed-in person.
        R(null, @"/api/auth/(me|logout|change-password)", AppRoles.Viewer),

        // Secrets and settings. Registries carry a plaintext password and AWS profiles carry
        // secret access keys, so their reads need Admin just like credentials (API-004, R5).
        R("GET", @"/api/resources/credentials(/.*)?", AppRoles.Admin),
        R("GET", @"/api/resources/registries(/.*)?", AppRoles.Admin),
        R("GET", @"/api/resources/aws-profiles(/.*)?", AppRoles.Admin),
        // Key files: metadata is safe for every signed-in role to read (the .pem content is never returned),
        // so a Viewer can see the tab and a member can pick one when authorizing; changing them needs Operator.
        R("GET", @"/api/resources/key-files(/.*)?", AppRoles.Viewer),
        R(null, @"/api/resources/key-files(/.*)?", AppRoles.Operator),
        R("GET", @"/api/resources(/.*)?", AppRoles.Viewer),
        R(null, @"/api/resources(/.*)?", AppRoles.Admin),

        // Servers: reading is open to every role, changing a server or its keys is Admin only.
        R("GET", @"/api/servers/[^/]+/(ssh/ops|db/ops)/.+/stream", AppRoles.Operator),
        R("GET", @"/api/servers/[^/]+/container-logs/stream", AppRoles.Operator),
        R("GET", @"/api/servers(/.*)?", AppRoles.Viewer),
        R("POST", @"/api/servers/[^/]+/(ssh/exec|db/(backup|restore|query|query-raw))", AppRoles.Operator),
        R("DELETE", @"/api/servers/[^/]+/db/backups", AppRoles.Operator),
        R(null, @"/api/servers(/.*)?", AppRoles.Admin),

        // Project settings are Admin only; building and deploying is Operator.
        R("POST", @"/api/projects/[^/]+/(repos/[^/]+/(purge-history|delete-files)|compose-repo/clone-url|ssh-key/(generate|authorize))", AppRoles.Admin),
        R("POST", @"/api/projects/[^/]+/build-source", AppRoles.Admin),
        R("POST", @"/api/projects", AppRoles.Admin),
        R("POST", @"/api/projects/detect", AppRoles.Admin),
        R("PUT", @"/api/projects/[^/]+", AppRoles.Admin),
        R("DELETE", @"/api/projects/[^/]+", AppRoles.Admin),

        // Operations that stream output of work somebody started: not for read-only people.
        R("GET", @"/api/projects/[^/]+/(ssh/ops|db/ops)/.+/stream", AppRoles.Operator),
        R("GET", @"/api/projects/[^/]+/container-logs/stream", AppRoles.Operator),
        R("GET", @"/api/projects/[^/]+/db/backups/(download|open-folder)", AppRoles.Operator),
        R("GET", @"/api/repos/ops/[^/]+/stream", AppRoles.Operator),

        // Scheduling changes are settings.
        R("POST", @"/api/scheduler/(schedules|replay)", AppRoles.Admin),
        R("DELETE", @"/api/scheduler/schedules/.+", AppRoles.Admin),
    ];

    // Returns the least role allowed, or null when the call is public (health check and the web app's own files).
    public static string? RequiredRole(string method, string path)
    {
        var trimmed = path.Length > 1 ? path.TrimEnd('/') : path;

        if (!trimmed.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)) return null;
        if (trimmed.Equals("/api/health", StringComparison.OrdinalIgnoreCase) && HttpMethods.IsGet(method)) return null;

        foreach (var rule in Rules)
        {
            if (rule.Method is not null && !rule.Method.Equals(method, StringComparison.OrdinalIgnoreCase)) continue;
            if (rule.Path.IsMatch(trimmed)) return rule.Role;
        }

        return HttpMethods.IsGet(method) || HttpMethods.IsHead(method) ? AppRoles.Viewer : AppRoles.Operator;
    }

    // While a temporary password is still in place only these calls work.
    public static bool AllowedWhilePasswordChangeIsPending(string path) =>
        path.TrimEnd('/') is "/api/auth/me" or "/api/auth/logout" or "/api/auth/change-password";
}
