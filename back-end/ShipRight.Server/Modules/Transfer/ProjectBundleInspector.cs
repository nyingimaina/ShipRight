namespace ShipRight.Modules.Transfer;

/// <summary>Reads a bundle and reports what importing it would do, writing nothing.</summary>
public class ProjectBundleInspector(TransferStores stores, string currentVersion)
{
    public async Task<BundleInspection> InspectAsync(byte[] bytes)
    {
        var manifest = ProjectBundleReader.ReadManifest(bytes);
        if (manifest.FormatVersion > BundleManifest.CurrentFormatVersion)
            return new BundleInspection
            {
                ProjectName = manifest.ProjectName, Manifest = manifest, Importable = false,
                Errors = [ProjectBundleReader.NewerMessage(manifest)],
            };

        var bundle = ProjectBundleReader.Read(bytes);
        var warnings = new List<BundleWarning>(manifest.Warnings);

        if (await stores.Projects.GetByNameAsync(bundle.Project.Name) is not null)
            warnings.Add(new BundleWarning
            {
                Code = "name-clash", Location = "project",
                Message = $"A project named '{bundle.Project.Name}' already exists here. The import is added alongside it under a new name.",
            });
        if (manifest.ShipRightVersion != currentVersion)
            warnings.Add(new BundleWarning
            {
                Severity = "info", Code = "version-differs", Location = "bundle",
                Message = $"Exported from ShipRight {manifest.ShipRightVersion}; this is {currentVersion}.",
            });

        return new BundleInspection
        {
            ProjectName = bundle.Project.Name,
            Manifest = manifest,
            Importable = true,
            Warnings = warnings,
            Counts = new Dictionary<string, int>
            {
                ["servers"] = bundle.Server is null ? 0 : 1,
                ["registries"] = bundle.Registries.Count,
                ["awsProfiles"] = bundle.AwsProfiles.Count,
                ["credentials"] = bundle.Credentials.Count,
                ["scripts"] = bundle.Scripts.Count,
                ["pipelines"] = bundle.Pipelines.Count,
                ["services"] = bundle.Project.Services.Count,
            },
        };
    }
}
