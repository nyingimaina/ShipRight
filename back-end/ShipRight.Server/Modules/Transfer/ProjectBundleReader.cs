using System.IO.Compression;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using ShipRight.Modules.Projects;
using ShipRight.Modules.Resources.Models;

namespace ShipRight.Modules.Transfer;

/// <summary>
/// Parses a bundle into memory. Entries are looked up by the names the manifest lists and matched
/// against a strict pattern, so a crafted zip cannot make the importer read or write anywhere else.
/// </summary>
public static class ProjectBundleReader
{
    private const int MaxEntries = 500;
    private const long MaxUncompressedBytes = 50L * 1024 * 1024;

    private static readonly Regex ResourceEntry =
        new(@"^resources/(registries|aws-profiles|credentials|scripts|pipelines)/[0-9a-fA-F\-]{36}\.json$", RegexOptions.Compiled);

    public static BundleManifest ReadManifest(byte[] bytes)
    {
        using var zip = Open(bytes);
        return ReadManifest(zip);
    }

    public static ProjectBundle Read(byte[] bytes)
    {
        using var zip = Open(bytes);
        var manifest = ReadManifest(zip);
        if (manifest.FormatVersion > BundleManifest.CurrentFormatVersion)
            throw new BundleException(NewerMessage(manifest));

        foreach (var name in manifest.Files)
            if (zip.GetEntry(name) is null)
                throw new BundleException($"The bundle is incomplete: '{name}' is listed but missing.");

        var project = Load<ProjectConfig>(zip, "project.json")
            ?? throw new BundleException("The bundle has no project.json.");
        var bundle = new ProjectBundle
        {
            Manifest = manifest,
            Project = project,
            Server = manifest.Files.Contains("server.json") ? Load<ServerConfig>(zip, "server.json") : null,
        };
        foreach (var name in manifest.Files.Where(f => ResourceEntry.IsMatch(f)))
        {
            switch (name.Split('/')[1])
            {
                case "registries": bundle.Registries.Add(Load<DockerRegistryResource>(zip, name)!); break;
                case "aws-profiles": bundle.AwsProfiles.Add(Load<AwsProfileResource>(zip, name)!); break;
                case "credentials": bundle.Credentials.Add(Load<CredentialResource>(zip, name)!); break;
                case "scripts": bundle.Scripts.Add(Load<ScriptResource>(zip, name)!); break;
                case "pipelines": bundle.Pipelines.Add(Load<PipelineResource>(zip, name)!); break;
            }
        }
        return bundle;
    }

    public static string NewerMessage(BundleManifest m) =>
        $"This bundle was made by a newer ShipRight ({m.ShipRightVersion}, bundle format {m.FormatVersion}). Update this ShipRight to import it.";

    private static ZipArchive Open(byte[] bytes)
    {
        try
        {
            var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            if (zip.Entries.Count > MaxEntries || zip.Entries.Sum(e => e.Length) > MaxUncompressedBytes)
            {
                zip.Dispose();
                throw new BundleException("The bundle is too large to be a ShipRight project export.");
            }
            return zip;
        }
        catch (InvalidDataException)
        {
            throw new BundleException("This is not a ShipRight export (it is not a valid zip file).");
        }
    }

    private static BundleManifest ReadManifest(ZipArchive zip) =>
        Load<BundleManifest>(zip, "manifest.json")
            ?? throw new BundleException("This is not a ShipRight export (manifest.json is missing).");

    private static T? Load<T>(ZipArchive zip, string name) where T : class
    {
        var entry = zip.GetEntry(name);
        if (entry is null) return null;
        try
        {
            using var reader = new StreamReader(entry.Open());
            return JsonConvert.DeserializeObject<T>(reader.ReadToEnd());
        }
        catch (JsonException ex)
        {
            throw new BundleException($"'{name}' in the bundle is damaged: {ex.Message}");
        }
    }
}
