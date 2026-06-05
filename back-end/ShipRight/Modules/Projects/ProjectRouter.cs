using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Serilog;
using ShipRight.Modules.VersionFiles;

namespace ShipRight.Modules.Projects;

public static class ProjectRouter
{
    private static readonly Regex DockerImageRegex =
        new(@"^[a-z0-9._\-\/]+$", RegexOptions.Compiled);

    public static void MapProjectRoutes(this WebApplication app)
    {
        app.MapGet("/api/projects", async (IProjectStore store) =>
            Results.Ok(await store.GetAllAsync()));

        app.MapPost("/api/projects/detect", (DetectRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.RootPath))
                return Results.BadRequest(new { isError = true, message = "rootPath is required." });
            if (req.RootPath.Contains("..") || req.RootPath.Contains('\0'))
                return Results.BadRequest(new { isError = true, message = "Invalid path." });
            if (!Directory.Exists(req.RootPath))
                return Results.NotFound(new { isError = true, message = $"Directory not found: {req.RootPath}" });

            Log.Information("Detecting project config from {RootPath}", req.RootPath);
            var result = ProjectDetector.Detect(req.RootPath);
            return Results.Ok(result);
        });

        app.MapGet("/api/projects/{id}", async (string id, IProjectStore store) =>
        {
            var project = await store.GetByIdAsync(id);
            return project is null
                ? Results.NotFound(Error($"Project '{id}' not found."))
                : Results.Ok(project);
        });

        app.MapPost("/api/projects", async (ProjectConfig input, IProjectStore store) =>
        {
            var errors = await ValidateAsync(input, store, isNew: true);
            if (errors.Count > 0) return Results.BadRequest(errors);

            var id = await GenerateUniqueIdAsync(input.Name, store);
            var project = input with { Id = id, CreatedAt = DateTime.UtcNow, ModifiedAt = DateTime.UtcNow };
            await store.SaveAsync(project);
            Log.Information("Project created: {ProjectId} ({ProjectName})", project.Id, project.Name);
            return Results.Created($"/api/projects/{project.Id}", project);
        });

        app.MapPut("/api/projects/{id}", async (string id, ProjectConfig input, IProjectStore store) =>
        {
            var existing = await store.GetByIdAsync(id);
            if (existing is null)
                return Results.NotFound(Error($"Project '{id}' not found."));

            var errors = await ValidateAsync(input with { Id = id }, store, isNew: false);
            if (errors.Count > 0) return Results.BadRequest(errors);

            var updated = input with { Id = id, CreatedAt = existing.CreatedAt, ModifiedAt = DateTime.UtcNow };
            await store.SaveAsync(updated);
            Log.Information("Project updated: {ProjectId}", id);
            return Results.Ok(updated);
        });

        app.MapDelete("/api/projects/{id}", async (string id, IProjectStore store) =>
        {
            var existing = await store.GetByIdAsync(id);
            if (existing is null)
                return Results.NotFound(Error($"Project '{id}' not found."));

            await store.DeleteAsync(id);
            Log.Information("Project deleted: {ProjectId}", id);
            return Results.NoContent();
        });

        app.MapGet("/api/projects/{id}/current-versions", async (string id, IProjectStore store) =>
        {
            var project = await store.GetByIdAsync(id);
            if (project is null) return Results.NotFound(Error($"Project '{id}' not found."));

            var results = new List<object>();
            foreach (var svc in project.Services)
            {
                try
                {
                    var v = await VersionFileService.ReadAsync(svc);
                    results.Add(new
                    {
                        serviceName = v.ServiceName,
                        version = v.Version,
                        suggestedNext = VersionFileService.SuggestNext(v.Version),
                        versionFilePath = v.VersionFilePath,
                        error = (string?)null
                    });
                }
                catch (Exception ex)
                {
                    results.Add(new
                    {
                        serviceName = svc.Name,
                        version = (string?)null,
                        suggestedNext = (string?)null,
                        versionFilePath = svc.VersionFilePath,
                        error = ex.Message
                    });
                }
            }
            return Results.Ok(results);
        });
    }

    private static object Error(string message, string? field = null) =>
        field is null
            ? new { isError = true, message }
            : new { isError = true, message, field };

    private static async Task<string> GenerateUniqueIdAsync(string name, IProjectStore store)
    {
        var slug = new string(name.ToLowerInvariant()
            .Replace(' ', '-')
            .Where(c => char.IsLetterOrDigit(c) || c == '-')
            .ToArray());
        if (string.IsNullOrEmpty(slug)) return Guid.NewGuid().ToString("N")[..8];

        if (await store.GetByIdAsync(slug) is null) return slug;
        for (int i = 2; i < 100; i++)
        {
            var candidate = $"{slug}-{i}";
            if (await store.GetByIdAsync(candidate) is null) return candidate;
        }
        return Guid.NewGuid().ToString("N")[..8];
    }

    private static async Task<List<object>> ValidateAsync(ProjectConfig p, IProjectStore store, bool isNew)
    {
        var errors = new List<object>();
        void Err(string field, string msg) => errors.Add(Error(msg, field));
        bool IsLinux() => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

        // Name
        if (string.IsNullOrWhiteSpace(p.Name))
            Err("name", "Project name is required.");
        else if (p.Name.Length > 100)
            Err("name", "Project name must be 100 characters or fewer.");
        else
        {
            var dup = await store.GetByNameAsync(p.Name);
            if (dup is not null && dup.Id != p.Id)
                Err("name", $"A project named '{p.Name}' already exists.");
        }

        // Services
        if (p.Services.Count == 0)
            Err("services", "At least one service is required.");
        else if (p.Services.Count > 10)
            Err("services", "A project can have at most 10 services.");
        else
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < p.Services.Count; i++)
            {
                var s = p.Services[i];
                string F(string f) => $"services[{i}].{f}";

                if (string.IsNullOrWhiteSpace(s.Name)) Err(F("name"), "Service name is required.");
                else if (s.Name.Length > 100) Err(F("name"), "Service name must be 100 characters or fewer.");
                else if (!names.Add(s.Name)) Err(F("name"), $"Duplicate service name '{s.Name}'.");

                ValidatePath(s.VersionFilePath, F("versionFilePath"), errors, mustExistAsFile: IsLinux());
                ValidatePath(s.BuildContextPath, F("buildContextPath"), errors, mustExistAsDir: IsLinux());

                if (string.IsNullOrWhiteSpace(s.DockerImageName))
                    Err(F("dockerImageName"), "Docker image name is required.");
                else if (s.DockerImageName.Contains(':'))
                    Err(F("dockerImageName"), "Docker image name must not include a tag (remove the colon and tag).");
                else if (!DockerImageRegex.IsMatch(s.DockerImageName))
                    Err(F("dockerImageName"), "Docker image name contains invalid characters (use lowercase letters, digits, '.', '-', '/', '_' only).");
            }
        }

        // Git
        if (string.IsNullOrWhiteSpace(p.Git.RepoPath))
            Err("git.repoPath", "Git repository path is required.");
        else
        {
            ValidatePath(p.Git.RepoPath, "git.repoPath", errors);
            if (IsLinux() && !Directory.Exists(Path.Combine(p.Git.RepoPath, ".git")))
                Err("git.repoPath", $"No .git directory found at '{p.Git.RepoPath}'.");
        }

        if (string.IsNullOrWhiteSpace(p.Git.DeployBranch))
            Err("git.deployBranch", "Deploy branch is required.");
        else if (p.Git.DeployBranch.Length > 100)
            Err("git.deployBranch", "Deploy branch must be 100 characters or fewer.");

        // WSL
        if (string.IsNullOrWhiteSpace(p.Wsl.WorkingDir))
            Err("wsl.workingDir", "WSL working directory is required.");
        else if (!p.Wsl.WorkingDir.StartsWith('/'))
            Err("wsl.workingDir", "WSL working directory must be an absolute Linux path starting with '/'.");

        // Server
        if (string.IsNullOrWhiteSpace(p.Server.Host)) Err("server.host", "Host is required.");
        else if (p.Server.Host.Length > 253) Err("server.host", "Host must be 253 characters or fewer.");

        if (string.IsNullOrWhiteSpace(p.Server.Username)) Err("server.username", "Username is required.");
        else if (p.Server.Username.Contains(' ')) Err("server.username", "Username must not contain spaces.");
        else if (p.Server.Username.Length > 100) Err("server.username", "Username must be 100 characters or fewer.");

        if (string.IsNullOrWhiteSpace(p.Server.SshKeyPath))
            Err("server.sshKeyPath", "SSH key path is required.");
        else
        {
            ValidatePath(p.Server.SshKeyPath, "server.sshKeyPath", errors, mustExistAsFile: IsLinux());
            if (IsLinux() && File.Exists(p.Server.SshKeyPath))
            {
                try
                {
                    var mode = File.GetUnixFileMode(p.Server.SshKeyPath);
                    if ((mode & (UnixFileMode.GroupRead | UnixFileMode.OtherRead)) != 0)
                        errors.Add(new { isError = false, field = "server.sshKeyPath",
                            message = $"SSH key has insecure permissions. Run: chmod 600 {p.Server.SshKeyPath}" });
                }
                catch { /* Permissions check unavailable */ }
            }
        }

        if (string.IsNullOrWhiteSpace(p.Server.RemoteWorkingDir))
            Err("server.remoteWorkingDir", "Remote working directory is required.");
        else if (!p.Server.RemoteWorkingDir.StartsWith('/'))
            Err("server.remoteWorkingDir", "Remote working directory must start with '/'.");

        if (string.IsNullOrWhiteSpace(p.Server.RebuildScript))
            Err("server.rebuildScript", "Rebuild script name is required.");
        else if (p.Server.RebuildScript.Contains('/') || p.Server.RebuildScript.Contains('\\'))
            Err("server.rebuildScript", "Rebuild script name must not contain path separators.");
        else if (p.Server.RebuildScript.Length > 100)
            Err("server.rebuildScript", "Rebuild script name must be 100 characters or fewer.");

        return errors;
    }

    private static void ValidatePath(string path, string field, List<object> errors,
        bool mustExistAsFile = false, bool mustExistAsDir = false)
    {
        void Err(string msg) => errors.Add(Error(msg, field));
        if (string.IsNullOrWhiteSpace(path)) { Err("Path is required."); return; }
        if (path.Length > 4096) { Err("Path exceeds 4096 characters."); return; }
        if (path.Contains('\0')) { Err("Path contains invalid null characters."); return; }
        if (path.Contains("..")) { Err("Path traversal ('..') is not allowed."); return; }

        if (mustExistAsFile && !File.Exists(path)) Err($"File not found: {path}");
        if (mustExistAsDir && !Directory.Exists(path)) Err($"Directory not found: {path}");
    }
}
