using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Database;
using ShipRight.Modules.Database.Providers;
using ShipRight.Modules.Projects;
using ShipRight.Shared.Events;
using ShipRight.Shared.SshRunner;
using ShipRight.Shared.Store;

namespace ShipRight.Tests.Modules.Database;

[TestClass]
public class DatabaseOrchestratorBackupEncodingTests : IDisposable
{
    private readonly string _projectId = $"bom-test-{Guid.NewGuid():N}";
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    public void Dispose()
    {
        var dir = Path.Combine(DataDirectory.Resolve(), "backups", _projectId);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    [TestMethod]
    public async Task BackupAsync_StreamStdoutProvider_WritesFileWithoutUtf8Bom()
    {
        var orchestrator = new DatabaseOrchestrator(
            new FakeResolver(), new DumpingSshRunner(), new KnownHostsStore(), new BuildEventBus());

        await orchestrator.BackupAsync(ProjectFor(_projectId), "op-1", default);

        var file = SingleBackupFile(_projectId);
        var firstBytes = ReadFirstBytes(file, 3);
        CollectionAssert.AreNotEqual(Utf8Bom, firstBytes, "Backup file must not start with a UTF-8 BOM.");
    }

    [TestMethod]
    public async Task BackupAsync_StreamStdoutProvider_FirstLineIsTheMariaDbCommentUnbroken()
    {
        var orchestrator = new DatabaseOrchestrator(
            new FakeResolver(), new DumpingSshRunner(), new KnownHostsStore(), new BuildEventBus());

        await orchestrator.BackupAsync(ProjectFor(_projectId), "op-1", default);

        var file = SingleBackupFile(_projectId);
        var firstLine = File.ReadLines(file).First();
        Assert.AreEqual("-- MariaDB dump 10.17  Distrib 10.5.5-MariaDB, for debian-linux-gnu (x86_64)", firstLine);
    }

    [TestMethod]
    public async Task ScheduledBackupAsync_StreamStdoutProvider_WritesFileWithoutUtf8Bom()
    {
        var orchestrator = new DatabaseOrchestrator(
            new FakeResolver(), new DumpingSshRunner(), new KnownHostsStore(), new BuildEventBus());

        var result = await orchestrator.ScheduledBackupAsync(ProjectFor(_projectId), default);

        Assert.IsTrue(result);
        var file = SingleBackupFile(_projectId);
        var firstBytes = ReadFirstBytes(file, 3);
        CollectionAssert.AreNotEqual(Utf8Bom, firstBytes, "Scheduled backup file must not start with a UTF-8 BOM.");
    }

    private static byte[] ReadFirstBytes(string path, int count)
    {
        using var fs = File.OpenRead(path);
        var buffer = new byte[count];
        _ = fs.Read(buffer, 0, count);
        return buffer;
    }

    private static string SingleBackupFile(string projectId)
    {
        var dir = Path.Combine(DataDirectory.Resolve(), "backups", projectId);
        var files = Directory.GetFiles(dir, "*.sql");
        Assert.AreEqual(1, files.Length, "Expected exactly one backup file to be written.");
        return files[0];
    }

    private static ProjectConfig ProjectFor(string id) => new()
    {
        Id = id,
        Name = $"Project-{id}",
        Database = new DatabaseConfig
        {
            Provider = DbProviderType.MariaDb,
            DatabaseName = "test-db",
            ContainerName = "db",
        },
        Server = new ServerConfig { Host = "localhost", Username = "test" },
        CreatedAt = DateTime.UtcNow,
    };

    private sealed class FakeResolver : IDbProviderResolver
    {
        public IDbProvider Resolve(DbProviderType providerType) => new StreamStdoutProvider();
    }

    private sealed class StreamStdoutProvider : IDbProvider
    {
        public string ProviderName => "MariaDB";
        public string PasswordEnvVar => "MYSQL_PWD";
        public string BackupExtension => ".sql";
        public BackupTransfer Transfer => BackupTransfer.StreamStdout;

        public string BackupCommand(DatabaseConfig cfg) => "dump";
        public string BackupFilePath(DatabaseConfig cfg, string opId) => throw new NotSupportedException();
        public string RestoreCommand(DatabaseConfig cfg, string remoteFilePath) => throw new NotSupportedException();
        public string QueryCommand(DatabaseConfig cfg, string remoteFilePath) => throw new NotSupportedException();
        public string CleanupCommand(string remoteFilePath) => throw new NotSupportedException();
        public string ListDatabasesCommand(DatabaseConfig cfg) => throw new NotSupportedException();
    }

    // Mimics a real mariadb-dump: first line is the "-- MariaDB dump ..." comment header.
    private sealed class DumpingSshRunner : ISshRunner
    {
        public async Task<int> RunAsync(string host, string username, string keyPath,
            string command, Func<string, Task>? onOutput = null,
            Func<string, Task>? onStderr = null, CancellationToken ct = default)
        {
            if (onOutput is not null)
            {
                await onOutput("-- MariaDB dump 10.17  Distrib 10.5.5-MariaDB, for debian-linux-gnu (x86_64)");
                await onOutput("--");
                await onOutput("CREATE TABLE `example` (`id` int NOT NULL);");
            }
            return 0;
        }
    }
}
