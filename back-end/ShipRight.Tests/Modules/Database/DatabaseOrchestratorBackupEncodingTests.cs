using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Database;
using ShipRight.Modules.Database.Providers;
using ShipRight.Modules.Projects;
using ShipRight.Modules.Servers;
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

    [TestMethod]
    public async Task BackupAsync_ProjectServerHasNoKey_UsesTheKeyFromTheLinkedServerStore()
    {
        var opId = "op-key";
        var store = new MemoryServerStore(new ServerConfig
        {
            Id = "srv-1",
            Host = "server.example",
            Username = "ubuntu",
            SshKeyPath = "/keys/managed.pem",
        });
        var ssh = new CapturingSshRunner();
        var orchestrator = new DatabaseOrchestrator(
            new FakeResolver(), ssh, new KnownHostsStore(), new BuildEventBus());
        var service = new DatabaseBackupService(orchestrator, store);
        var project = ProjectFor(_projectId) with
        {
            ServerId = "srv-1",
            Server = new ServerConfig { Host = "server.example", Username = "ubuntu", SshKeyPath = "" },
        };

        await service.BackupAsync(project, opId, default);

        Assert.AreEqual("/keys/managed.pem", ssh.KeyPaths.Single(),
            "the linked server store is the source of truth when the project copy has no key");
    }

    [TestMethod]
    public async Task BackupAsync_NoKeyAnywhere_EmitsAClearErrorAndDoesNotConnect()
    {
        var opId = "op-no-key";
        var bus = new BuildEventBus();
        bus.Register(opId);
        var reader = bus.Subscribe(opId);
        var ssh = new CapturingSshRunner();
        var orchestrator = new DatabaseOrchestrator(
            new FakeResolver(), ssh, new KnownHostsStore(), bus);
        var service = new DatabaseBackupService(orchestrator, new MemoryServerStore(), bus);

        await service.BackupAsync(ProjectFor(_projectId) with
        {
            Server = new ServerConfig { Host = "server.example", Username = "ubuntu", SshKeyPath = "" },
        }, opId, default);

        var messages = new List<string>();
        while (reader.TryRead(out var message)) messages.Add(message);
        StringAssert.Contains(string.Join("\n", messages), "SSH key");
        Assert.AreEqual(0, ssh.KeyPaths.Count, "a backup must not try to connect without a key");
    }

    [TestMethod]
    public async Task ScheduledBackupAsync_NoKeyAnywhere_ReturnsFalseAndDoesNotConnect()
    {
        var ssh = new CapturingSshRunner();
        var orchestrator = new DatabaseOrchestrator(
            new FakeResolver(), ssh, new KnownHostsStore(), new BuildEventBus());
        var service = new DatabaseBackupService(orchestrator, new MemoryServerStore());

        var result = await service.ScheduledBackupAsync(ProjectFor(_projectId) with
        {
            Server = new ServerConfig { Host = "server.example", Username = "ubuntu", SshKeyPath = "" },
        }, default);

        Assert.IsFalse(result);
        Assert.AreEqual(0, ssh.KeyPaths.Count);
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

    private sealed class MemoryServerStore(params ServerConfig[] servers) : IServerStore
    {
        public Task<List<ServerConfig>> GetAllAsync() => Task.FromResult(servers.ToList());
        public Task<ServerConfig?> GetByIdAsync(string id) => Task.FromResult(servers.FirstOrDefault(s => s.Id == id));
        public Task SaveAsync(ServerConfig server) => Task.CompletedTask;
        public Task DeleteAsync(string id) => Task.CompletedTask;
    }

    private sealed class CapturingSshRunner : ISshRunner
    {
        public List<string> KeyPaths { get; } = [];

        public async Task<int> RunAsync(string host, string username, string keyPath,
            string command, Func<string, Task>? onOutput = null,
            Func<string, Task>? onStderr = null, CancellationToken ct = default)
        {
            KeyPaths.Add(keyPath);
            if (onOutput is not null) await onOutput("-- MariaDB dump");
            return 0;
        }
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
