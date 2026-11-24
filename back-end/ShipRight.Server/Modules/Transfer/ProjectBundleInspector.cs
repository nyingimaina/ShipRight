namespace ShipRight.Modules.Transfer;

/// <summary>Reads a bundle and reports what importing it would do, writing nothing.</summary>
public class ProjectBundleInspector(TransferStores stores, string currentVersion, string? passphrase = null)
{
    public async Task<BundleInspection> InspectAsync(byte[] bytes)
    {
        var plain = BundleCrypto.Decrypt(bytes, passphrase);
        var manifest = ProjectBundleReader.ReadManifest(plain);
        if (manifest.FormatVersion > BundleManifest.CurrentFormatVersion)
            return new BundleInspection
            {
                ProjectName = manifest.ProjectName, Manifest = manifest, Importable = false,
                Errors = [ProjectBundleReader.NewerMessage(manifest)],
            };

        var bundle = ProjectBundleReader.Read(plain);
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
            SecretsIncluded = manifest.SecretsIncluded,
            Warnings = warnings,
            RequiredInputs = manifest.Portability?.RequiredInputs ?? [],
            Counts = new Dictionary<string, int>
            {
                ["servers"] = bundle.Server is null ? 0 : 1,
                ["registries"] = bundle.Registries.Count,
                ["awsProfiles"] = bundle.AwsProfiles.Count,
                ["credentials"] = bundle.Credentials.Count,
                ["keyFiles"] = bundle.KeyFiles.Count,
                ["scripts"] = bundle.Scripts.Count,
                ["pipelines"] = bundle.Pipelines.Count,
                ["jit"] = bundle.Jit is null ? 0 : 1,
                ["services"] = bundle.Project.Services.Count,
            },
        };
    }
}
