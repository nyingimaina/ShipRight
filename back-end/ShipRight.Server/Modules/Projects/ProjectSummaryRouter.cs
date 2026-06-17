using ShipRight.Modules.Builds;
using ShipRight.Modules.VersionFiles;

namespace ShipRight.Modules.Projects;

public static class ProjectSummaryRouter
{
    public static void MapProjectSummaryRoutes(this WebApplication app)
    {
        app.MapGet("/api/projects/{id}/build-stats", async (string id, IBuildStore buildStore) =>
        {
            // Load up to 20 completed builds (any terminal-success status) in chronological order
            var builds = await buildStore.QueryAsync(
                id,
                "ImageBuilt,PushSucceeded,Deployed",
                null, null, null, 1, 20);
            builds.Reverse(); // oldest first for EWMA

            if (builds.Count == 0)
                return Results.Ok(new { sampleCount = 0, stageExpected = new Dictionary<string, int>(), totalBuildExpected = (int?)null, totalPushExpected = (int?)null, totalDeployExpected = (int?)null });

            // Per-stage EWMA (α = 0.3) across completed step durations
            var ewma = new Dictionary<string, double>();
            foreach (var build in builds)
            {
                foreach (var (step, seconds) in build.StepDurations)
                {
                    if (ewma.TryGetValue(step, out var prev))
                        ewma[step] = 0.3 * seconds + 0.7 * prev;
                    else
                        ewma[step] = seconds;
                }
            }

            var stageExpected = ewma.ToDictionary(kv => kv.Key, kv => (int)Math.Round(kv.Value));

            static int? SumSteps(Dictionary<string, int> d, IEnumerable<string> keys)
            {
                var total = 0; var any = false;
                foreach (var k in keys)
                    if (d.TryGetValue(k, out var v)) { total += v; any = true; }
                return any ? total : null;
            }

            string[] buildSteps = ["PreconditionCheck", "GitStatusCheck", "BranchCheck", "WriteVersionsAndTag", "ComposeRepoSync", "DockerBuild", "BuildComplete"];
            string[] pushSteps  = ["DockerLoginCheck", "DockerPush", "PushComplete"];
            string[] deploySteps = ["Deploy"];

            return Results.Ok(new
            {
                sampleCount = builds.Count,
                stageExpected,
                totalBuildExpected  = SumSteps(stageExpected, buildSteps),
                totalPushExpected   = SumSteps(stageExpected, pushSteps),
                totalDeployExpected = SumSteps(stageExpected, deploySteps),
            });
        });

        app.MapGet("/api/projects/{id}/summary", async (string id, IProjectStore projectStore, IBuildStore buildStore) =>
        {
            var project = await projectStore.GetByIdAsync(id);
            if (project is null)
                return Results.NotFound(new { isError = true, message = $"Project '{id}' not found." });

            // Current versions
            var currentVersions = new List<object>();
            foreach (var svc in project.Services)
            {
                try
                {
                    var v = await VersionFileService.ReadAsync(svc);
                    currentVersions.Add(new { serviceName = v.ServiceName, version = v.Version, error = (string?)null });
                }
                catch (Exception ex)
                {
                    currentVersions.Add(new { serviceName = svc.Name, version = (string?)null, error = ex.Message });
                }
            }

            // Build history for this project
            var recentBuilds = await buildStore.QueryAsync(id, null, null, null, null, 1, 10);

            var lastBuild = recentBuilds.FirstOrDefault();
            var lastDeployed = recentBuilds.FirstOrDefault(b => b.Status == BuildStatus.Deployed);

            var successCount = recentBuilds.Count(b =>
                b.Status == BuildStatus.PushSucceeded || b.Status == BuildStatus.Deployed);
            var successRate = recentBuilds.Count > 0
                ? (int)Math.Round(100.0 * successCount / recentBuilds.Count)
                : 0;

            return Results.Ok(new
            {
                projectId = project.Id,
                projectName = project.Name,
                currentVersions,
                lastBuild = lastBuild is null ? null : new
                {
                    id = lastBuild.Id,
                    status = lastBuild.Status.ToString(),
                    gitTag = lastBuild.GitTag,
                    startedAt = lastBuild.StartedAt,
                    completedAt = lastBuild.CompletedAt,
                },
                lastDeployedAt = lastDeployed?.DeployedAt,
                lastDeployedTag = lastDeployed?.GitTag,
                buildSuccessRate = successRate,
                recentBuildCount = recentBuilds.Count,
                hasDatabase = project.Database is not null,
                databaseName = project.Database?.DatabaseName,
            });
        });
    }
}
