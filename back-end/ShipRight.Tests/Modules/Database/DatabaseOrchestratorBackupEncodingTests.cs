using System.Text;
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
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sr-orch-" + Guid.NewGuid().ToString("N"));
    private readonly BackupStorage _storage;
    private readonly BackupKeyring _keyring;

    private static readonly string Dump =
        "-- MariaDB dump 10.17  Distrib 10.5.5-MariaDB, for debian-linux-gnu (x86_64)\n" +
        "--\n" +
        "CREATE TABLE `example` (`id` int NOT NULL);\n";

    public DatabaseOrchestratorBackupEncodingTests()
    {
        Directory.CreateDirectory(_root);
        _storage = BackupStorage.At(Path.Combine(_root, "backups"));
        _keyring = new BackupKeyring(Path.Combine(_root, "data"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private DatabaseOrchestrator Orchestrator(ISshRunner? ssh = null, IRawSshRunner? raw = null) =>
        new(new FakeResolver(), ssh ?? new CapturingSshRunner(), new KnownHostsStore(), new BuildEventBus(),
            networks: null, rawSsh: raw ?? new DumpingRawRunner(), storage: _storage, keyring: _keyring);

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
        Server = new ServerConfig { Host = "localhost", Username = "test", SshKeyPath = "/keys/test.pem" },
        CreatedAt = DateTime.UtcNow,
    };

    private async Task<byte[]> ReadBackAsync(BackupArtifactMeta meta)
    {
        var (stream, _) = await BackupArchive.OpenReadAsync(_storage, _projectId, meta.FileName, _keyring);
        await using (stream)
        {
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy);
            return copy.ToArray();
        }
    }

    [TestMethod]
    public async Task BackupAsync_StreamStdoutProvider_StoresTheExactDumpBytes()
    {
        await Orchestrator().BackupAsync(ProjectFor(_projectId), "op-1", default);

        var meta = BackupArchive.List(_storage, _projectId).Single();
        Assert.AreEqual(BackupCompression.Zstd, meta.Compression);
        Assert.AreEqual(BackupEncryption.AesGcm, meta.Encryption);
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(Dump), await ReadBackAsync(meta));
    }

    [TestMethod]
    public async Task BackupAsync_FirstLineIsTheMariaDbCommentUnbroken()
    {
        await Orchestrator().BackupAsync(ProjectFor(_projectId), "op-2", default);
        var meta = BackupArchive.List(_storage, _projectId).Single();

        var (stream, _) = await BackupArchive.OpenReadAsync(_storage, _projectId, meta.FileName, _keyring);
        await using (stream)
        using (var reader = new StreamReader(stream, Encoding.UTF8))
        {
            Assert.AreEqual(
                "-- MariaDB dump 10.17  Distrib 10.5.5-MariaDB, for debian-linux-gnu (x86_64)",
                await reader.ReadLineAsync());
        }
    }

    [TestMethod]
    public async Task ScheduledBackupAsync_StreamStdoutProvider_WritesAnArtifact()
    {
        var result = await Orchestrator().ScheduledBackupAsync(ProjectFor(_projectId), default);

        Assert.IsTrue(result);
        var meta = BackupArchive.List(_storage, _projectId).Single();
        Assert.AreEqual(BackupSource.Schedule, meta.Source);
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(Dump), await ReadBackAsync(meta));
    }

    [TestMethod]
    public async Task BackupAsync_ProjectServerHasNoKey_UsesTheKeyFromTheLinkedServerStore()
    {
        var raw = new DumpingRawRunner();
        var orchestrator = Orchestrator(raw: raw);
        var store = new MemoryServerStore(new ServerConfig
        {
            Id = "srv-1",
            Host = "server.example",
            Username = "ubuntu",
            SshKeyPath = "/keys/managed.pem",
        });
        var service = new DatabaseBackupService(orchestrator, store);
        var project = ProjectFor(_projectId) with
        {
            ServerId = "srv-1",
            Server = new ServerConfig { Host = "server.example", Username = "ubuntu", SshKeyPath = "" },
        };

        await service.BackupAsync(project, "op-key", default);

        Assert.AreEqual("/keys/managed.pem", raw.KeyPaths.Single(),
            "the linked server store is the source of truth when the project copy has no key");
    }

    [TestMethod]
    public async Task BackupAsync_NoKeyAnywhere_EmitsAClearErrorAndDoesNotConnect()
    {
        var opId = "op-no-key";
        var bus = new BuildEventBus();
        bus.Register(opId);
        var reader = bus.Subscribe(opId);
        var raw = new DumpingRawRunner();
        var orchestrator = new DatabaseOrchestrator(
            new FakeResolver(), new CapturingSshRunner(), new KnownHostsStore(), bus,
            networks: null, rawSsh: raw, storage: _storage, keyring: _keyring);
        var service = new DatabaseBackupService(orchestrator, new MemoryServerStore(), bus);

        await service.BackupAsync(ProjectFor(_projectId) with
        {
            Server = new ServerConfig { Host = "server.example", Username = "ubuntu", SshKeyPath = "" },
        }, opId, default);

        var messages = new List<string>();
        while (reader.TryRead(out var message)) messages.Add(message);
        StringAssert.Contains(string.Join("\n", messages), "SSH key");
        Assert.AreEqual(0, raw.KeyPaths.Count, "a backup must not try to connect without a key");
    }

    [TestMethod]
    public async Task ScheduledBackupAsync_NoKeyAnywhere_ReturnsFalseAndDoesNotConnect()
    {
        var raw = new DumpingRawRunner();
        var orchestrator = new DatabaseOrchestrator(
            new FakeResolver(), new CapturingSshRunner(), new KnownHostsStore(), new BuildEventBus(),
            networks: null, rawSsh: raw, storage: _storage, keyring: _keyring);
        var service = new DatabaseBackupService(orchestrator, new MemoryServerStore());

        var result = await service.ScheduledBackupAsync(ProjectFor(_projectId) with
        {
            Server = new ServerConfig { Host = "server.example", Username = "ubuntu", SshKeyPath = "" },
        }, default);

        Assert.IsFalse(result);
        Assert.AreEqual(0, raw.KeyPaths.Count);
    }

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

        public Task<int> RunAsync(string host, string username, string keyPath,
            string command, Func<string, Task>? onOutput = null,
            Func<string, Task>? onStderr = null, CancellationToken ct = default)
        {
            KeyPaths.Add(keyPath);
            return Task.FromResult(0);
        }
    }

    private sealed class DumpingRawRunner : IRawSshRunner
    {
        public List<string> KeyPaths { get; } = [];

        public async Task<int> RunAsync(string host, string username, string keyPath, string command,
            Stream stdout, Func<string, Task>? onStderr = null, CancellationToken ct = default)
        {
            KeyPaths.Add(keyPath);
            await stdout.WriteAsync(Encoding.UTF8.GetBytes(Dump), ct);
            return 0;
        }
    }
}
