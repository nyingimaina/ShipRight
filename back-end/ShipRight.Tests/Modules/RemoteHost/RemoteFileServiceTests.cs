using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Projects;
using ShipRight.Modules.RemoteHost;
using ShipRight.Shared.SshRunner;

namespace ShipRight.Tests.Modules.RemoteHost;

[TestClass]
public class RemoteFileServiceTests
{
    private sealed class FakeClient : IRemoteFileClient
    {
        public Dictionary<string, List<RemoteEntry>> Dirs { get; } = new();
        public Dictionary<string, byte[]> Files { get; } = new();
        public Dictionary<string, RemoteEntry> Stats { get; } = new();
        public List<string> Reads { get; } = [];
        public Dictionary<string, byte[]> Written { get; } = new();
        public bool ReadThrows;

        public Task<IReadOnlyList<RemoteEntry>> ListAsync(ServerConfig s, string path, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<RemoteEntry>>(Dirs.TryGetValue(path, out var l) ? l : []);
        public Task<RemoteEntry?> StatAsync(ServerConfig s, string path, CancellationToken ct)
            => Task.FromResult(Stats.TryGetValue(path, out var e) ? e : null);
        public async Task ReadAsync(ServerConfig s, string path, Stream dest, CancellationToken ct)
        {
            Reads.Add(path);
            if (ReadThrows) throw new IOException("read failed");
            await dest.WriteAsync(Files[path], ct);
        }
        public async Task WriteAsync(ServerConfig s, string path, Stream src, CancellationToken ct)
        {
            using var ms = new MemoryStream();
            await src.CopyToAsync(ms, ct);
            Written[path] = ms.ToArray();
        }
    }

    private sealed class FakeSsh : ISshRunner
    {
        public List<string> Commands { get; } = [];
        public Func<string, int> ExitFor { get; set; } = _ => 0;
        public Task<int> RunAsync(string host, string username, string keyPath, string command,
            Func<string, Task>? onOutput = null, Func<string, Task>? onStderr = null, CancellationToken ct = default)
        {
            Commands.Add(command);
            return Task.FromResult(ExitFor(command));
        }
    }

    private static ServerConfig Server() => new()
    {
        Id = "s1", Host = "h", Username = "ec2-user", SshKeyPath = "/k",
        RemoteWorkingDir = "/home/ec2-user/ship-right",
    };

    private static RemoteEntry F(string dir, string name, long size = 1) => new(name, $"{dir}/{name}", false, size, null);
    private static RemoteEntry D(string dir, string name) => new(name, $"{dir}/{name}", true, 0, null);

    private static (RemoteFileService svc, FakeClient client, FakeSsh ssh) Make()
    {
        var c = new FakeClient(); var s = new FakeSsh();
        return (new RemoteFileService(c, s), c, s);
    }

    private static async Task<RemoteFileException> Throws(Func<Task> act)
    {
        try { await act(); }
        catch (RemoteFileException ex) { return ex; }
        Assert.Fail("expected RemoteFileException");
        return null!;
    }

    // ── Paths ────────────────────────────────────────────────────────────────

    [TestMethod]
    public void NormalizePath_CollapsesDotsAndSlashes_AndRejectsRelativeOrNul()
    {
        Assert.AreEqual("/a/c", RemoteFileService.NormalizePath("/a//b/../c/."));
        Assert.AreEqual("/", RemoteFileService.NormalizePath("/a/.."));
        Assert.ThrowsException<RemoteFileException>(() => RemoteFileService.NormalizePath("relative/x"));
        Assert.ThrowsException<RemoteFileException>(() => RemoteFileService.NormalizePath("/a\0b"));
    }

    [TestMethod]
    public void ShellQuote_EscapesSingleQuotes()
    {
        Assert.AreEqual("'it'\\''s'", RemoteFileService.ShellQuote("it's"));
    }

    // ── List ─────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task List_DefaultsToWorkingDir_DirsFirst_HidesDotfiles_AndGivesParent()
    {
        var (svc, c, _) = Make();
        var dir = "/home/ec2-user/ship-right";
        c.Dirs[dir] = [F(dir, "b.txt"), D(dir, "Zeta"), F(dir, ".env"), D(dir, "alpha")];

        var r = await svc.ListAsync(Server(), null, showHidden: false);

        Assert.AreEqual(dir, r.Path);
        Assert.AreEqual("/home/ec2-user", r.Parent);
        CollectionAssert.AreEqual(new[] { "alpha", "Zeta", "b.txt" }, r.Entries.Select(e => e.Name).ToArray());
    }

    [TestMethod]
    public async Task List_ShowHidden_IncludesDotfiles()
    {
        var (svc, c, _) = Make();
        c.Dirs["/srv"] = [F("/srv", ".env")];
        var r = await svc.ListAsync(Server(), "/srv", showHidden: true);
        Assert.AreEqual(1, r.Entries.Count);
        Assert.IsNull((await svc.ListAsync(Server(), "/", false)).Parent);
    }

    [TestMethod]
    public async Task List_RelativePath_IsRejected()
    {
        var (svc, _, _) = Make();
        await Throws(() => svc.ListAsync(Server(), "etc", false));
    }

    // ── Open (read text) ─────────────────────────────────────────────────────

    [TestMethod]
    public async Task ReadText_ReturnsContent()
    {
        var (svc, c, _) = Make();
        c.Stats["/srv/a.log"] = F("/srv", "a.log", 5);
        c.Files["/srv/a.log"] = Encoding.UTF8.GetBytes("hello");
        Assert.AreEqual("hello", await svc.ReadTextAsync(Server(), "/srv/a.log"));
    }

    [TestMethod]
    public async Task ReadText_TooLarge_IsRefusedWithoutReading()
    {
        var (svc, c, _) = Make();
        c.Stats["/srv/big.log"] = F("/srv", "big.log", RemoteFileService.MaxReadBytes + 1);
        var ex = await Throws(() => svc.ReadTextAsync(Server(), "/srv/big.log"));
        StringAssert.Contains(ex.Message, "too large");
        Assert.AreEqual(0, c.Reads.Count);
    }

    [TestMethod]
    public async Task ReadText_Binary_IsRefused()
    {
        var (svc, c, _) = Make();
        c.Stats["/srv/x.bin"] = F("/srv", "x.bin", 3);
        c.Files["/srv/x.bin"] = [1, 0, 2];
        var ex = await Throws(() => svc.ReadTextAsync(Server(), "/srv/x.bin"));
        StringAssert.Contains(ex.Message, "binary");
    }

    [TestMethod]
    public async Task ReadText_Directory_OrMissing_IsRefused()
    {
        var (svc, c, _) = Make();
        c.Stats["/srv/d"] = D("/srv", "d");
        await Throws(() => svc.ReadTextAsync(Server(), "/srv/d"));
        var ex = await Throws(() => svc.ReadTextAsync(Server(), "/srv/none"));
        Assert.AreEqual(404, ex.StatusCode);
    }

    // ── Download ─────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task DownloadName_FileKeepsName_DirectoryGetsTarGz()
    {
        var (svc, c, _) = Make();
        c.Stats["/srv/a.log"] = F("/srv", "a.log");
        c.Stats["/srv/logs"] = D("/srv", "logs");
        Assert.AreEqual("a.log", await svc.DownloadNameAsync(Server(), "/srv/a.log"));
        Assert.AreEqual("logs.tar.gz", await svc.DownloadNameAsync(Server(), "/srv/logs"));
    }

    [TestMethod]
    public async Task Download_File_StreamsDirectly_WithoutSsh()
    {
        var (svc, c, ssh) = Make();
        c.Stats["/srv/a.log"] = F("/srv", "a.log");
        c.Files["/srv/a.log"] = [1, 2, 3];
        using var ms = new MemoryStream();
        await svc.DownloadAsync(Server(), "/srv/a.log", ms);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, ms.ToArray());
        Assert.AreEqual(0, ssh.Commands.Count);
    }

    [TestMethod]
    public async Task Download_Directory_CompressesToTemp_StreamsIt_ThenCleansUp()
    {
        var (svc, c, ssh) = Make();
        c.Stats["/srv/my dir"] = D("/srv", "my dir");
        c.Files.Clear();
        string? tmp = null;
        ssh.ExitFor = cmd => { if (cmd.StartsWith("tar ")) { tmp = ExtractTmp(cmd); c.Files[tmp] = [9, 9]; } return 0; };

        using var ms = new MemoryStream();
        await svc.DownloadAsync(Server(), "/srv/my dir", ms);

        CollectionAssert.AreEqual(new byte[] { 9, 9 }, ms.ToArray());
        StringAssert.Contains(ssh.Commands[0], "tar -czf");
        StringAssert.Contains(ssh.Commands[0], "-C '/srv'");
        StringAssert.Contains(ssh.Commands[0], "'my dir'");
        Assert.IsTrue(ssh.Commands[1].StartsWith("rm -f "));
        StringAssert.Contains(ssh.Commands[1], tmp!);
    }

    [TestMethod]
    public async Task Download_Directory_CleansUpEvenWhenReadFails()
    {
        var (svc, c, ssh) = Make();
        c.Stats["/srv/logs"] = D("/srv", "logs");
        c.ReadThrows = true;
        using var ms = new MemoryStream();
        await Assert.ThrowsExceptionAsync<IOException>(() => svc.DownloadAsync(Server(), "/srv/logs", ms));
        Assert.IsTrue(ssh.Commands.Last().StartsWith("rm -f "));
    }

    [TestMethod]
    public async Task Download_Directory_TarFailure_IsReported()
    {
        var (svc, c, ssh) = Make();
        c.Stats["/srv/logs"] = D("/srv", "logs");
        ssh.ExitFor = cmd => cmd.StartsWith("tar ") ? 2 : 0;
        using var ms = new MemoryStream();
        var ex = await Throws(() => svc.DownloadAsync(Server(), "/srv/logs", ms));
        StringAssert.Contains(ex.Message, "compress");
    }

    private static string ExtractTmp(string cmd)
    {
        var start = cmd.IndexOf("/tmp/", StringComparison.Ordinal);
        var end = cmd.IndexOf(".tar.gz", start, StringComparison.Ordinal) + ".tar.gz".Length;
        return cmd[start..end];
    }

    // ── Upload ───────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Upload_WritesIntoDirectory()
    {
        var (svc, c, _) = Make();
        c.Stats["/srv"] = D("", "srv");
        await svc.UploadAsync(Server(), "/srv", "a.txt", new MemoryStream([7, 8]));
        CollectionAssert.AreEqual(new byte[] { 7, 8 }, c.Written["/srv/a.txt"]);
    }

    [DataTestMethod]
    [DataRow("")]
    [DataRow("../evil")]
    [DataRow("a/b")]
    [DataRow("..")]
    public async Task Upload_BadFileName_IsRejected(string name)
    {
        var (svc, c, _) = Make();
        c.Stats["/srv"] = D("", "srv");
        await Throws(() => svc.UploadAsync(Server(), "/srv", name, new MemoryStream([1])));
        Assert.AreEqual(0, c.Written.Count);
    }

    [TestMethod]
    public async Task Upload_TargetNotADirectory_IsRejected()
    {
        var (svc, c, _) = Make();
        c.Stats["/srv/f"] = F("/srv", "f");
        await Throws(() => svc.UploadAsync(Server(), "/srv/f", "a.txt", new MemoryStream([1])));
    }

    [TestMethod]
    public async Task Upload_OverTheSizeCap_IsRejected()
    {
        var (svc, c, _) = Make();
        c.Stats["/srv"] = D("", "srv");
        var big = new MemoryStream(new byte[20]);
        var ex = await Throws(() => svc.UploadAsync(Server(), "/srv", "a.bin", big, maxBytes: 10));
        StringAssert.Contains(ex.Message, "too large");
    }

    // ── Delete ───────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Delete_RunsQuotedRm()
    {
        var (svc, c, ssh) = Make();
        c.Stats["/home/ec2-user/ship-right/old logs"] = D("", "x");
        await svc.DeleteAsync(Server(), "/home/ec2-user/ship-right/old logs");
        Assert.AreEqual("rm -rf -- '/home/ec2-user/ship-right/old logs'", ssh.Commands.Single());
    }

    [DataTestMethod]
    [DataRow("/")]
    [DataRow("/home")]
    [DataRow("/etc")]
    [DataRow("/home/ec2-user")]
    [DataRow("/home/ec2-user/ship-right")]
    [DataRow("/home/ec2-user/ship-right/..")]
    [DataRow("/home/ec2-user/ship-right/x/../..")]
    public async Task Delete_ProtectedPaths_AreRefused(string path)
    {
        var (svc, _, ssh) = Make();
        var ex = await Throws(() => svc.DeleteAsync(Server(), path));
        Assert.AreEqual(403, ex.StatusCode);
        Assert.AreEqual(0, ssh.Commands.Count);
    }

    [TestMethod]
    public async Task Delete_MissingPath_Is404()
    {
        var (svc, _, _) = Make();
        var ex = await Throws(() => svc.DeleteAsync(Server(), "/home/ec2-user/ship-right/none"));
        Assert.AreEqual(404, ex.StatusCode);
    }
}
