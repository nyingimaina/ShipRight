using System.Text.RegularExpressions;
using ShipRight.Modules.Resources.Stores;

namespace ShipRight.Modules.JitDeploy;

/// <summary>A user name and access token for reading a private https repository. It never prints itself.</summary>
public sealed record JitGitSecret(string Username, string Token)
{
    public override string ToString() => "JitGitSecret(hidden)";
}

/// <summary>
/// Gives git a saved access token for exactly one command, without the token ever being part of an address, a saved remote, a
/// setting, a log or a message. The token is written into a helper file that only its owner can read, in a private temporary folder;
/// git asks the helper for it; the folder is removed as soon as the command ends, however it ends. Git's own credential helpers are
/// switched off for the command so nothing can store the token. The token may only contain ordinary token characters, so no quoting
/// can go wrong.
/// </summary>
public static class JitGitCredential
{
    private static readonly Regex TokenShape = new(@"^[A-Za-z0-9._~+/=-]{8,512}$", RegexOptions.Compiled);
    private static readonly Regex UserShape = new(@"^[A-Za-z0-9._-]{1,64}$", RegexOptions.Compiled);

    /// <summary>Null when the token can be used; otherwise a plain message that never repeats it.</summary>
    public static string? TokenProblem(string? token) =>
        token is not null && TokenShape.IsMatch(token)
            ? null
            : "The saved credential's value is empty, too short, or has characters that cannot be used safely here (only letters, digits and . _ ~ + / = - are allowed).";

    public static string UsernameFor(string? hostPattern, string repoUrl)
    {
        var azure = (hostPattern ?? "").Contains("azure", StringComparison.OrdinalIgnoreCase)
                    || repoUrl.Contains("dev.azure.com", StringComparison.OrdinalIgnoreCase)
                    || repoUrl.Contains("visualstudio.com", StringComparison.OrdinalIgnoreCase);
        return azure ? "pat" : "token";
    }

    public static string Wrap(string command, JitGitSecret secret)
    {
        if (TokenProblem(secret.Token) is { } problem) throw new ArgumentException(problem);
        if (!UserShape.IsMatch(secret.Username)) throw new ArgumentException("The credential's user name is not valid.");

        return "d=$(mktemp -d) && chmod 700 \"$d\" && trap 'rm -rf \"$d\"' EXIT && " +
               "printf '%s\\n' '#!/bin/sh' 'case \"$1\" in *sername*) printf \"%s\\n\" " + secret.Username + " ;; *) printf \"%s\\n\" " + secret.Token + " ;; esac' > \"$d/ask\" && " +
               "chmod 700 \"$d/ask\" && " +
               "export GIT_ASKPASS=\"$d/ask\" GIT_TERMINAL_PROMPT=0 GIT_CONFIG_COUNT=1 GIT_CONFIG_KEY_0=credential.helper GIT_CONFIG_VALUE_0= && " +
               "{ " + command + " ; }";
    }
}

/// <summary>Finds the saved credential a project chose. Every way it cannot be used becomes a plain message that never contains the token.</summary>
public static class JitCredentialResolver
{
    public static async Task<(JitGitSecret? Secret, string? Error)> ResolveAsync(Guid id, string repoUrl, ICredentialResourceStore? store, string? projectId)
    {
        if (!Regex.IsMatch(repoUrl ?? "", @"^https://", RegexOptions.IgnoreCase))
            return (null, "A saved credential only works with an https address. For an SSH address use a deploy key.");
        var credential = store is null ? null : await store.GetByIdAsync(id);
        if (credential is null || (credential.ProjectId is not null && credential.ProjectId != projectId))
            return (null, "The saved credential no longer exists, or is not available to this project. Pick another or use a deploy key.");
        var token = credential.Value?.Trim();
        if (JitGitCredential.TokenProblem(token) is { } problem) return (null, problem);
        return (new JitGitSecret(JitGitCredential.UsernameFor(credential.HostPattern, repoUrl!), token!), null);
    }

    /// <summary>Adds the project's chosen credential to a deploy request. A project that chose none is left exactly as it was.</summary>
    public static async Task<(JitDeployRequest? Request, string? Error)> ApplyAsync(JitDeployRequest request, JitDeployConfig? cfg, ICredentialResourceStore? store, string projectId)
    {
        if (cfg?.CredentialResourceId is not { } id) return (request, null);
        var (secret, error) = await ResolveAsync(id, request.RepoUrl, store, projectId);
        return error is not null ? (null, error) : (request with { GitCredential = secret }, null);
    }
}

/// <summary>The names of the saved credentials a project may use, for a dropdown. Values are never sent.</summary>
public static class JitCredentialRoutes
{
    public static void MapJitCredentialRoutes(this WebApplication app) =>
        app.MapGet("/api/jit/{id}/credentials", (string id, ICredentialResourceStore credentials) => HandleListAsync(id, credentials));

    internal static async Task<IResult> HandleListAsync(string projectId, ICredentialResourceStore? store)
    {
        if (store is null) return Results.Ok(Array.Empty<object>());
        var all = await store.GetAllAsync();
        return Results.Ok(all
            .Where(c => c.ProjectId is null || c.ProjectId == projectId)
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(c => new { id = c.Id, name = c.Name, hostPattern = c.HostPattern })
            .ToList());
    }
}
