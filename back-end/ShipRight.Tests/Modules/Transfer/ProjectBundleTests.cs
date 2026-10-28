using System.IO.Compression;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using ShipRight.Modules.Projects;
using ShipRight.Modules.Resources.Models;
using ShipRight.Modules.Resources.Stores;
using ShipRight.Modules.Servers;
using ShipRight.Modules.Transfer;

namespace ShipRight.Tests.Modules.Transfer;

[TestClass]
public class ProjectBundleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"sr_transfer_{Guid.NewGuid():N}");
    private readonly TransferStores _source;
    private readonly TransferStores _target;

    private static readonly Guid RegistryId = Guid.NewGuid();
    private static readonly Guid AwsId = Guid.NewGuid();
    private static readonly Guid CredId = Guid.NewGuid();
    private static readonly Guid RebuildScriptId = Guid.NewGuid();
    private static readonly Guid PipelineScriptId = Guid.NewGuid();
    private static readonly Guid PipelineId = Guid.NewGuid();
    private static readonly Guid UnrelatedScriptId = Guid.NewGuid();

    public ProjectBundleTests()
    {
        _source = NewStores("source");
        _target = NewStores("target");
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private TransferStores NewStores(string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        return new TransferStores(
            new SqliteProjectStore(dir), new SqliteServerStore(dir),
            new SqliteDockerRegistryResourceStore(dir), new SqliteAwsProfileResourceStore(dir),
            new SqliteCredentialResourceStore(dir), new SqliteScriptResourceStore(dir),
            new SqlitePipelineResourceStore(dir));
    }

    // A project wired to every kind of related record, with secrets and machine paths sprinkled in.
    private async Task SeedAsync()
    {
        await _source.AwsProfiles.SaveAsync(new AwsProfileResource
        {
            Id = AwsId, Name = "prod-aws", ProfileName = "work", AccessKeyId = "AKIA-SECRET", SecretAccessKey = "aws-secret", SessionToken = "tok", DefaultRegion = "us-east-2",
        });
        await _source.Registries.SaveAsync(new DockerRegistryResource
        {
            Id = RegistryId, Name = "prod-ecr", Registry = "123.dkr.ecr.us-east-2.amazonaws.com", Password = "reg-secret",
            AuthType = RegistryAuthType.AwsEcr, AwsProfileResourceId = AwsId,
        });
        await _source.Credentials.SaveAsync(new CredentialResource { Id = CredId, Name = "github-pat", Value = "ghp_secret", HostPattern = "github.com" });
        await _source.Scripts.SaveAsync(new ScriptResource { Id = RebuildScriptId, Name = "rebuild", Content = "echo rebuild", Target = ExecutionTarget.Remote });
        await _source.Scripts.SaveAsync(new ScriptResource
        {
            Id = PipelineScriptId, Name = "pre-build", Content = "echo pre", WorkingDirectory = @"D:\work\app", Scope = PipelineScope.Project, ProjectId = "app",
        });
        await _source.Scripts.SaveAsync(new ScriptResource { Id = UnrelatedScriptId, Name = "someone-elses", Content = "echo other" });
        await _source.Pipelines.SaveAsync(new PipelineResource
        {
            Id = PipelineId, Name = "app-pipeline",
            Steps = [new PipelineStep { Type = PipelineStepType.Script, ScriptResourceId = PipelineScriptId, WorkingDirectory = @"D:\work\app" }, new PipelineStep { Type = PipelineStepType.Build }],
        });
        await _source.Servers.SaveAsync(new ServerConfig
        {
            Id = "srv-1", Name = "Prod box", Host = "10.0.0.5", Username = "ubuntu", SshKeyPath = @"C:\Users\me\.ssh\prod.pem",
            RemoteWorkingDir = "/srv/app", EnvFile = "DB_PASSWORD=hunter2", ComposeRepoUrl = "https://ghp_urltoken@github.com/acme/compose.git", ManagedSshKey = true, ManagedSshKeyAuthorized = true,
            RebuildScriptResourceId = RebuildScriptId, PipelineResourceId = PipelineId,
        });
        await _source.Projects.SaveAsync(new ProjectConfig
        {
            Id = "app", Name = "App", ServerId = "srv-1",
            Services =
            [
                new ServiceConfig
                {
                    Name = "api", VersionFilePath = @"D:\work\app\api\version.txt", BuildContextPath = @"D:\work\app\api",
                    DockerImageName = "123.dkr.ecr.us-east-2.amazonaws.com/api", DockerPassword = "svc-secret", DockerRegistryResourceId = RegistryId,
                },
            ],
            GitRepos = [new GitConfig { RepoPath = @"D:\work\app", CredentialResourceId = CredId }],
            Wsl = new WslConfig { WorkingDir = "/mnt/d/work/app-compose", ComposeRepoUrl = "https://user:urlpass@github.com/acme/compose.git" },
            Database = new DatabaseConfig { ContainerName = "db", DatabaseName = "app", RootPassword = "db-secret" },
        });
    }

    private async Task<byte[]> ExportAsync(TransferStores? from = null) =>
        (await new ProjectBundleExporter(from ?? _source, "4.4.14").ExportAsync("app"))!;

    private static string[] EntryNames(byte[] bundle)
    {
        using var zip = new ZipArchive(new MemoryStream(bundle), ZipArchiveMode.Read);
        return zip.Entries.Select(e => e.FullName).ToArray();
    }

    private static byte[] Rewrite(byte[] bundle, Action<ZipArchive> edit)
    {
        var ms = new MemoryStream();
        ms.Write(bundle);
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Update, leaveOpen: true)) edit(zip);
        return ms.ToArray();
    }

    private static string ReadEntry(byte[] bundle, string name)
    {
        using var zip = new ZipArchive(new MemoryStream(bundle), ZipArchiveMode.Read);
        using var r = new StreamReader(zip.GetEntry(name)!.Open());
        return r.ReadToEnd();
    }

    // ── Export ────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Export_UnknownProject_ReturnsNull()
    {
        Assert.IsNull(await new ProjectBundleExporter(_source, "4.4.14").ExportAsync("nope"));
    }

    [TestMethod]
    public async Task Export_WritesOneFilePerRecord_FollowingEveryRelationFromTheProject()
    {
        await SeedAsync();
        var names = EntryNames(await ExportAsync());

        CollectionAssert.IsSubsetOf(new[]
        {
            "manifest.json", "project.json", "server.json",
            $"resources/registries/{RegistryId}.json", $"resources/aws-profiles/{AwsId}.json",
            $"resources/credentials/{CredId}.json", $"resources/scripts/{RebuildScriptId}.json",
            $"resources/scripts/{PipelineScriptId}.json", $"resources/pipelines/{PipelineId}.json",
        }, names);
    }

    [TestMethod]
    public async Task Export_LeavesOutRecordsTheProjectDoesNotUse()
    {
        await SeedAsync();
        CollectionAssert.DoesNotContain(EntryNames(await ExportAsync()), $"resources/scripts/{UnrelatedScriptId}.json");
    }

    [TestMethod]
    public async Task Export_Manifest_RecordsVersionFormatAndProject()
    {
        await SeedAsync();
        var manifest = JsonConvert.DeserializeObject<BundleManifest>(ReadEntry(await ExportAsync(), "manifest.json"))!;
        Assert.AreEqual(BundleManifest.CurrentFormatVersion, manifest.FormatVersion);
        Assert.AreEqual("4.4.14", manifest.ShipRightVersion);
        Assert.AreEqual("app", manifest.ProjectId);
        Assert.AreEqual("App", manifest.ProjectName);
    }

    [TestMethod]
    public async Task Export_NeverWritesASecret()
    {
        await SeedAsync();
        var bundle = await ExportAsync();
        using var zip = new ZipArchive(new MemoryStream(bundle), ZipArchiveMode.Read);
        foreach (var entry in zip.Entries)
        {
            using var r = new StreamReader(entry.Open());
            var text = r.ReadToEnd();
            foreach (var secret in new[] { "svc-secret", "reg-secret", "aws-secret", "AKIA-SECRET", "ghp_secret", "db-secret", "hunter2", "ghp_urltoken", "urlpass" })
                Assert.IsFalse(text.Contains(secret), $"{entry.FullName} leaked '{secret}'");
        }
    }

    [TestMethod]
    public async Task Export_WarnsAboutEachOmittedSecretAndEachMachinePath()
    {
        await SeedAsync();
        var warnings = JsonConvert.DeserializeObject<BundleManifest>(ReadEntry(await ExportAsync(), "manifest.json"))!.Warnings;

        foreach (var code in new[] { "secret-omitted", "path", "ssh-key-not-transferred", "aws-profile-name" })
            Assert.IsTrue(warnings.Any(w => w.Code == code), $"missing warning '{code}'");
        Assert.IsTrue(warnings.Any(w => w.Code == "path" && w.Message.Contains(@"D:\work\app\api")), "service build path not flagged");
        Assert.IsTrue(warnings.Any(w => w.Code == "path" && w.Message.Contains(@"C:\Users\me\.ssh\prod.pem")), "ssh key path not flagged");
        Assert.IsTrue(warnings.Any(w => w.Code == "secret-omitted" && w.Location.Contains("github-pat")), "credential not flagged");
    }

    [TestMethod]
    public async Task Export_KeepsTheRepoAddressButDropsTheCredentialsEmbeddedInIt()
    {
        await SeedAsync();
        var bundle = await ExportAsync();
        Assert.AreEqual("https://github.com/acme/compose.git",
            JsonConvert.DeserializeObject<ServerConfig>(ReadEntry(bundle, "server.json"))!.ComposeRepoUrl);
        Assert.AreEqual("https://github.com/acme/compose.git",
            JsonConvert.DeserializeObject<ProjectConfig>(ReadEntry(bundle, "project.json"))!.Wsl.ComposeRepoUrl);

        var warnings = JsonConvert.DeserializeObject<BundleManifest>(ReadEntry(bundle, "manifest.json"))!.Warnings;
        Assert.IsTrue(warnings.Any(w => w.Code == "secret-omitted" && w.Message.Contains("repository URL")));
    }

    [TestMethod]
    public async Task Export_LeavesAnSshRepoAddressUntouched()
    {
        await SeedAsync();
        var server = (await _source.Servers.GetByIdAsync("srv-1"))! with { ComposeRepoUrl = "ssh://git@github.com/acme/compose.git" };
        await _source.Servers.SaveAsync(server);
        var bundle = await ExportAsync();
        Assert.AreEqual("ssh://git@github.com/acme/compose.git",
            JsonConvert.DeserializeObject<ServerConfig>(ReadEntry(bundle, "server.json"))!.ComposeRepoUrl);
    }

    // ── Inspect ───────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Inspect_ReportsWhatIsInside_WithoutChangingAnything()
    {
        await SeedAsync();
        var bundle = await ExportAsync();
        var inspection = await new ProjectBundleInspector(_target, "4.4.14").InspectAsync(bundle);

        Assert.AreEqual("App", inspection.ProjectName);
        Assert.IsTrue(inspection.Importable);
        Assert.AreEqual(1, inspection.Counts["servers"]);
        Assert.AreEqual(2, inspection.Counts["scripts"]);
        Assert.AreEqual(0, (await _target.Projects.GetAllAsync()).Count);
        Assert.AreEqual(0, (await _target.Servers.GetAllAsync()).Count);
    }

    [TestMethod]
    public async Task Inspect_FlagsANameClash_AndAVersionDifference()
    {
        await SeedAsync();
        var bundle = await ExportAsync();
        await _target.Projects.SaveAsync(new ProjectConfig { Id = "existing", Name = "App" });

        var inspection = await new ProjectBundleInspector(_target, "5.0.0").InspectAsync(bundle);

        Assert.IsTrue(inspection.Warnings.Any(w => w.Code == "name-clash"));
        Assert.IsTrue(inspection.Warnings.Any(w => w.Code == "version-differs"));
        Assert.IsTrue(inspection.Importable, "a clash is resolved by renaming, so it must not block the import");
    }

    [TestMethod]
    public async Task Inspect_BundleFromANewerFormat_IsNotImportable()
    {
        await SeedAsync();
        var bundle = Rewrite(await ExportAsync(), zip =>
        {
            var manifest = JsonConvert.DeserializeObject<BundleManifest>(ReadEntry(ToBytes(zip), "manifest.json"))!;
            zip.GetEntry("manifest.json")!.Delete();
            using var w = new StreamWriter(zip.CreateEntry("manifest.json").Open());
            w.Write(JsonConvert.SerializeObject(manifest with { FormatVersion = BundleManifest.CurrentFormatVersion + 1 }));
        });

        var inspection = await new ProjectBundleInspector(_target, "4.4.14").InspectAsync(bundle);

        Assert.IsFalse(inspection.Importable);
        Assert.IsTrue(inspection.Errors.Any(e => e.Contains("newer")));
    }

    [TestMethod]
    public async Task Inspect_NotAZip_Throws()
    {
        await Assert.ThrowsExceptionAsync<BundleException>(() =>
            new ProjectBundleInspector(_target, "4.4.14").InspectAsync(new byte[] { 1, 2, 3, 4 }));
    }

    [TestMethod]
    public async Task Inspect_ZipWithoutManifest_Throws()
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true)) zip.CreateEntry("hello.txt");
        await Assert.ThrowsExceptionAsync<BundleException>(() =>
            new ProjectBundleInspector(_target, "4.4.14").InspectAsync(ms.ToArray()));
    }

    // ── Import ────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Import_RecreatesTheProjectAndEverythingItDependsOn_RelinkedToNewIds()
    {
        await SeedAsync();
        var outcome = await new ProjectBundleImporter(_target).ImportAsync(await ExportAsync());

        var project = (await _target.Projects.GetByIdAsync(outcome.ProjectId))!;
        Assert.AreEqual("App", project.Name);

        var server = (await _target.Servers.GetByIdAsync(project.ServerId))!;
        Assert.AreEqual("Prod box", server.Name);
        Assert.AreNotEqual("srv-1", server.Id, "ids are remapped so an import can never overwrite existing records");

        var registry = (await _target.Registries.GetByIdAsync(project.Services[0].DockerRegistryResourceId!.Value))!;
        Assert.AreEqual("prod-ecr", registry.Name);
        Assert.AreNotEqual(RegistryId, registry.Id);
        Assert.AreEqual("work", (await _target.AwsProfiles.GetByIdAsync(registry.AwsProfileResourceId!.Value))!.ProfileName);

        Assert.AreEqual("github-pat", (await _target.Credentials.GetByIdAsync(project.GitRepos[0].CredentialResourceId!.Value))!.Name);
        Assert.AreEqual("rebuild", (await _target.Scripts.GetByIdAsync(server.RebuildScriptResourceId!.Value))!.Name);

        var pipeline = (await _target.Pipelines.GetByIdAsync(server.PipelineResourceId!.Value))!;
        var step = pipeline.Steps.First(s => s.Type == PipelineStepType.Script);
        Assert.AreEqual("pre-build", (await _target.Scripts.GetByIdAsync(step.ScriptResourceId!.Value))!.Name);
        Assert.AreNotEqual(PipelineScriptId, step.ScriptResourceId);
    }

    [TestMethod]
    public async Task Import_KeepsPathsAsExported_AndStartsWithEmptySecrets()
    {
        await SeedAsync();
        var outcome = await new ProjectBundleImporter(_target).ImportAsync(await ExportAsync());
        var project = (await _target.Projects.GetByIdAsync(outcome.ProjectId))!;

        Assert.AreEqual(@"D:\work\app\api", project.Services[0].BuildContextPath);
        Assert.AreEqual("", project.Services[0].DockerPassword);
        Assert.AreEqual("", project.Database!.RootPassword);
        Assert.AreEqual("", (await _target.Credentials.GetByIdAsync(project.GitRepos[0].CredentialResourceId!.Value))!.Value);
        Assert.AreEqual("", (await _target.Servers.GetByIdAsync(project.ServerId))!.EnvFile);
    }

    [TestMethod]
    public async Task Import_ServerKeyIsNotTransferred_SoItIsMarkedUnauthorised()
    {
        await SeedAsync();
        var outcome = await new ProjectBundleImporter(_target).ImportAsync(await ExportAsync());
        var server = (await _target.Servers.GetByIdAsync((await _target.Projects.GetByIdAsync(outcome.ProjectId))!.ServerId))!;
        Assert.IsFalse(server.ManagedSshKeyAuthorized);
    }

    [TestMethod]
    public async Task Import_ScopedRecords_FollowTheNewProjectId()
    {
        await SeedAsync();
        var outcome = await new ProjectBundleImporter(_target).ImportAsync(await ExportAsync());
        var scoped = (await _target.Scripts.GetByProjectAsync(outcome.ProjectId));
        Assert.AreEqual(1, scoped.Count);
        Assert.AreEqual("pre-build", scoped[0].Name);
    }

    [TestMethod]
    public async Task Import_IntoTheSameInstance_RenamesInsteadOfOverwriting()
    {
        await SeedAsync();
        var bundle = await ExportAsync();
        var outcome = await new ProjectBundleImporter(_source).ImportAsync(bundle);

        Assert.AreNotEqual("app", outcome.ProjectId);
        Assert.AreEqual("App (imported)", outcome.ProjectName);
        Assert.AreEqual("App", (await _source.Projects.GetByIdAsync("app"))!.Name);
        Assert.AreEqual(2, (await _source.Servers.GetAllAsync()).Count);
    }

    [TestMethod]
    public async Task Import_TwiceInARow_NeverCollides()
    {
        await SeedAsync();
        var bundle = await ExportAsync();
        var importer = new ProjectBundleImporter(_target);
        var a = await importer.ImportAsync(bundle);
        var b = await importer.ImportAsync(bundle);
        Assert.AreNotEqual(a.ProjectName, b.ProjectName);
        Assert.AreNotEqual(a.ProjectId, b.ProjectId);
    }

    [TestMethod]
    public async Task Import_ReturnsTheWarningsSoTheUserKnowsWhatToFix()
    {
        await SeedAsync();
        var outcome = await new ProjectBundleImporter(_target).ImportAsync(await ExportAsync());
        Assert.IsTrue(outcome.Warnings.Any(w => w.Code == "path"));
        Assert.IsTrue(outcome.Warnings.Any(w => w.Code == "secret-omitted"));
    }

    [TestMethod]
    public async Task Import_ABundleThatCannotBeImported_ChangesNothing()
    {
        await SeedAsync();
        var broken = Rewrite(await ExportAsync(), zip => zip.GetEntry("server.json")!.Delete());
        await Assert.ThrowsExceptionAsync<BundleException>(() => new ProjectBundleImporter(_target).ImportAsync(broken));
        Assert.AreEqual(0, (await _target.Projects.GetAllAsync()).Count);
        Assert.AreEqual(0, (await _target.Registries.GetAllAsync()).Count);
    }

    [TestMethod]
    public async Task Import_IgnoresEntriesWithUnexpectedNames()
    {
        await SeedAsync();
        var bundle = Rewrite(await ExportAsync(), zip =>
        {
            using var w = new StreamWriter(zip.CreateEntry("../../evil.json").Open());
            w.Write("{}");
        });
        var outcome = await new ProjectBundleImporter(_target).ImportAsync(bundle);
        Assert.IsNotNull(await _target.Projects.GetByIdAsync(outcome.ProjectId));
    }

    private static byte[] ToBytes(ZipArchive zip)
    {
        var ms = new MemoryStream();
        using (var copy = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var e in zip.Entries)
            {
                using var src = e.Open();
                using var dst = copy.CreateEntry(e.FullName).Open();
                src.CopyTo(dst);
            }
        return ms.ToArray();
    }
}
