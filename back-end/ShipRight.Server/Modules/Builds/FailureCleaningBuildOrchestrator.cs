using ShipRight.Modules.ComposeRepo;
using ShipRight.Modules.Projects;
using ShipRight.Modules.Resources;
using ShipRight.Modules.Resources.Stores;
using ShipRight.Shared.Events;
using ShipRight.Shared.ProcessRunner;
using ShipRight.Shared.SshRunner;

namespace ShipRight.Modules.Builds;

/// <summary>
/// A failed or cancelled docker build leaves garbage behind - untagged layers and cache that nothing will ever use.
/// The cleanup that follows a successful push never runs for those, so this clears them straight away, then lets the
/// original failure carry on exactly as it would have. Images the failed build did produce are kept on purpose: they
/// are what lets a retry skip rebuilding (and the project's space report lists them for a one-click clean).
/// </summary>
public class FailureCleaningBuildOrchestrator : SnapshotBuildOrchestrator
{
    private readonly PushAwareImagePruner? _pruner;

    public FailureCleaningBuildOrchestrator(
        IBuildStore buildStore, IProjectStore projectStore,
        BuildEventBus bus, IProcessRunner runner, ISshRunner ssh, ResourceResolutionService resourceResolution,
        IPipelineResourceStore pipelineStore, IScriptResourceStore scriptStore, ICredentialResourceStore credentialStore, ScriptExecutor scriptExecutor,
        RegistryRotationCoordinator? registryRotation = null, BuildMachineImagePruner? localImagePruner = null,
        ComposeRepoService? composeRepo = null)
        : base(buildStore, projectStore, bus, runner, ssh, resourceResolution, pipelineStore, scriptStore,
            credentialStore, scriptExecutor, registryRotation, localImagePruner, composeRepo)
    {
        _pruner = localImagePruner as PushAwareImagePruner;
    }

    protected override async Task RunDockerBuildAsync(PipelineContext ctx, BuildRecord record,
        ProjectConfig project, Func<Task> save, bool useBuildKit = false, CancellationToken ct = default)
    {
        try
        {
            await base.RunDockerBuildAsync(ctx, record, project, save, useBuildKit, ct);
        }
        catch (Exception)
        {
            if (_pruner is not null)
            {
                // The token may be the very thing that was cancelled, so the clean-up must not use it.
                await _pruner.PruneAfterFailureAsync(project, line => ctx.EmitLogAsync(line, "shipright"));
            }
            throw;
        }
    }
}
