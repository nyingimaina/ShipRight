using ShipRight.Modules.ComposeRepo;
using ShipRight.Modules.Projects;
using ShipRight.Modules.Resources;
using ShipRight.Modules.Resources.Stores;
using ShipRight.Modules.Setup;
using ShipRight.Shared.Events;
using ShipRight.Shared.ProcessRunner;
using ShipRight.Shared.SshRunner;

namespace ShipRight.Modules.Builds;

/// <summary>
/// A <see cref="BuildOrchestrator"/> that can build from a clean checkout of the committed code instead of the
/// project folder on disk. Projects that have not opted in (<see cref="BuildSource.CleanSnapshot"/>) go straight
/// to the original docker build, so nothing changes for them. For opted-in projects every repo is checked for
/// unsaved changes first - a dirty tree is refused, never built - then the exact commit is checked out inside
/// WSL, the docker build runs against that copy, and the copy is removed afterwards.
/// </summary>
public class SnapshotBuildOrchestrator : BuildOrchestrator
{
    private readonly CleanTreeGuard _guard;
    private readonly BuildSnapshotService _snapshots;

    public SnapshotBuildOrchestrator(
        IBuildStore buildStore, IProjectStore projectStore,
        BuildEventBus bus, IProcessRunner runner, ISshRunner ssh, ResourceResolutionService resourceResolution,
        IPipelineResourceStore pipelineStore, IScriptResourceStore scriptStore, ICredentialResourceStore credentialStore, ScriptExecutor scriptExecutor,
        RegistryRotationCoordinator? registryRotation = null, BuildMachineImagePruner? localImagePruner = null,
        ComposeRepoService? composeRepo = null)
        : base(buildStore, projectStore, bus, runner, ssh, resourceResolution, pipelineStore, scriptStore,
            credentialStore, scriptExecutor, registryRotation, localImagePruner, composeRepo)
    {
        _guard = new CleanTreeGuard(runner);
        _snapshots = new BuildSnapshotService(runner);
    }

    protected override async Task RunDockerBuildAsync(PipelineContext ctx, BuildRecord record,
        ProjectConfig project, Func<Task> save, bool useBuildKit = false, CancellationToken ct = default)
    {
        if (!project.UsesCleanSnapshot())
        {
            await base.RunDockerBuildAsync(ctx, record, project, save, useBuildKit, ct);
            return;
        }

        var building = project.Services.Where(s => record.Versions.Any(v => v.ServiceName == s.Name)).ToList();
        var repoOf = building.ToDictionary(s => s.Name, s => RepoFor(project, s));

        // Check every repo before checking anything out, so a problem in the second repo
        // cannot leave a checkout of the first lying around.
        var heads = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var repo in repoOf.Values.Select(r => r.RepoPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            await ctx.EmitLogAsync($"Checking that everything in {repo} is saved in git…", "shipright");
            var tree = await _guard.CheckAsync(repo, ct);
            if (tree.Error is not null)
                throw new InvalidOperationException($"Cannot build {repo}: {tree.Error}");
            if (!tree.IsClean)
                throw new InvalidOperationException(DirtyMessage(repo, tree));
            heads[repo] = tree.Sha!;
        }

        var taken = new List<(GitConfig Repo, BuildSnapshot Snapshot)>();
        try
        {
            foreach (var (repoPath, sha) in heads)
            {
                await ctx.EmitLogAsync(
                    $"Making a clean copy of {repoPath} at commit {sha[..7]} - only saved code goes into the build.", "shipright");
                var snapshot = await _snapshots.CreateAsync(repoPath, sha, SafeId(project.Id), SafeId(record.Id), ct);
                taken.Add((project.GitRepos.First(r => SameFolder(r.RepoPath, repoPath)), snapshot));
            }

            var mapped = project with
            {
                Services = project.Services.Select(s =>
                {
                    if (!repoOf.TryGetValue(s.Name, out var repo)) return s;
                    var snapshot = taken.First(t => SameFolder(t.Repo.RepoPath, repo.RepoPath)).Snapshot;
                    return s with { BuildContextPath = ContextInSnapshot(repo.RepoPath, s.BuildContextPath, snapshot.WorkDir) };
                }).ToList(),
            };

            await base.RunDockerBuildAsync(ctx, record, mapped, save, useBuildKit, ct);
        }
        finally
        {
            foreach (var (_, snapshot) in taken)
                await _snapshots.RemoveAsync(snapshot);
        }
    }

    private static GitConfig RepoFor(ProjectConfig project, ServiceConfig service)
    {
        var match = project.GitRepos
            .Where(r => Contains(r.RepoPath, service.BuildContextPath))
            .OrderByDescending(r => Normalize(r.RepoPath).Length)
            .FirstOrDefault();
        return match ?? throw new InvalidOperationException(
            $"Cannot build {service.Name} from a clean copy: its folder ({service.BuildContextPath}) is not inside any of this " +
            "project's git repositories. Add the repository to the project, or switch the project back to building from its folder.");
    }

    private static string DirtyMessage(string repo, CleanTreeResult tree)
    {
        var files = string.Join("\n", tree.Entries.Take(20).Select(e => $"  {e.Path} ({e.Description})"));
        var more = tree.Entries.Count > 20 ? $"\n  …and {tree.Entries.Count - 20} more" : "";
        return $"{repo} has changes that are not saved in git yet, so ShipRight will not build it. " +
               $"Please commit these first:\n{files}{more}";
    }

    internal static string ContextInSnapshot(string repoPath, string contextPath, string snapshotDir)
    {
        var rel = Normalize(contextPath)[Normalize(repoPath).Length..].TrimStart('/');
        return rel.Length == 0 ? snapshotDir : $"{snapshotDir}/{rel}";
    }

    private static bool Contains(string repoPath, string path)
    {
        var repo = Normalize(repoPath);
        var p = Normalize(path);
        return p.Equals(repo, StringComparison.OrdinalIgnoreCase) ||
               p.StartsWith(repo + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool SameFolder(string a, string b) =>
        Normalize(a).Equals(Normalize(b), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string path) => path.Replace('\\', '/').TrimEnd('/');

    /// <summary>Ids become folder names inside WSL, so anything outside a plain id is replaced.</summary>
    private static string SafeId(string id) =>
        new(id.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '-').ToArray());
}
