using ShipRight.Modules.Builds;
using ShipRight.Modules.VersionFiles;

namespace ShipRight.Modules.Projects;

public static class ProjectSummaryRouter
{
    public static void MapProjectSummaryRoutes(this WebApplication app)
    {
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
                b.Status == BuildStatus.BuildSucceeded || b.Status == BuildStatus.Deployed);
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
            });
        });
    }
}
