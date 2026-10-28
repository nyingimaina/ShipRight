using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Projects;

namespace ShipRight.Tests.Modules.Projects;

/// <summary>
/// Pins the CURRENT behaviour of ProjectDetector.Detect so the onboarding work can build
/// beside it without changing it. These tests describe what the code does today, including
/// quirks; a failure here means existing behaviour changed.
/// </summary>
[TestClass]
public class ProjectDetectorCharacterizationTests : IDisposable
{
    private readonly string _sandbox;
    private readonly string _root;

    public ProjectDetectorCharacterizationTests()
    {
        // The detector also scans sibling folders of the root for docker-compose.yml, so the
        // project lives one level down to keep that scan inside the sandbox.
        _sandbox = Path.Combine(Path.GetTempPath(), $"sr_detect_char_{Guid.NewGuid():N}");
        _root = Path.Combine(_sandbox, "proj");
        Directory.CreateDirectory(_root);
    }

    public void Dispose() => Directory.Delete(_sandbox, recursive: true);

    private string Dir(params string[] parts)
    {
        var path = Path.Combine(new[] { _root }.Concat(parts).ToArray());
        Directory.CreateDirectory(path);
        return path;
    }

    private static string Write(string dir, string name, string content)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private DetectedProjectConfig Detect() => ProjectDetector.Detect(_root, pullLatest: false);

    // ── Name suggestion ──────────────────────────────────────────────────────

    [TestMethod]
    public void SuggestedName_IsTheFolderNameWithDashesAndUnderscoresAsSpaces()
    {
        var root = Path.Combine(_sandbox, "my-cool_app");
        Directory.CreateDirectory(root);

        var detected = ProjectDetector.Detect(root, pullLatest: false);

        Assert.AreEqual("my cool app", detected.SuggestedName);
    }

    // ── Empty folder ─────────────────────────────────────────────────────────

    [TestMethod]
    public void EmptyFolder_FindsNothingAndReportsWhatIsMissing()
    {
        var detected = Detect();

        Assert.AreEqual(0, detected.Services.Count);
        Assert.AreEqual(0, detected.GitRepos.Count);
        Assert.IsNull(detected.WslWorkingDir);
        Assert.AreEqual(0, detected.Detected.Count);
        CollectionAssert.Contains(detected.Undetected, "Git repository path(s)");
        CollectionAssert.Contains(detected.Undetected, "Services (no version.txt + Dockerfile pairs found)");
        CollectionAssert.Contains(detected.Undetected, "WSL working directory (docker-compose location)");
    }

    [TestMethod]
    public void ServerFields_AreAlwaysReportedAsUndetected()
    {
        var detected = Detect();

        CollectionAssert.Contains(detected.Undetected, "Server host");
        CollectionAssert.Contains(detected.Undetected, "SSH key path");
        CollectionAssert.Contains(detected.Undetected, "Remote working directory");
    }

    // ── Service detection ────────────────────────────────────────────────────

    [TestMethod]
    public void VersionFileAndDockerfileInSameFolder_IsOneService()
    {
        var svc = Dir("api");
        Write(svc, "Dockerfile", "FROM scratch");
        var vf = Write(svc, "version.txt", "2.3.4");

        var detected = Detect();

        Assert.AreEqual(1, detected.Services.Count);
        Assert.AreEqual(svc, detected.Services[0].BuildContextPath);
        Assert.AreEqual(vf, detected.Services[0].VersionFilePath);
        Assert.AreEqual("2.3.4", detected.Services[0].Version);
        CollectionAssert.Contains(detected.Detected, "1 service(s) detected");
    }

    [TestMethod]
    public void VersionFileWithoutAnyDockerfile_IsNotAService()
    {
        Write(Dir("api"), "version.txt", "1.0.0");

        var detected = Detect();

        Assert.AreEqual(0, detected.Services.Count);
    }

    [TestMethod]
    public void DockerfileWithoutVersionFile_IsNotAService()
    {
        Write(Dir("api"), "Dockerfile", "FROM scratch");

        var detected = Detect();

        Assert.AreEqual(0, detected.Services.Count);
    }

    [TestMethod]
    public void DockerfileNameMatchIsCaseInsensitive()
    {
        var svc = Dir("api");
        Write(svc, "DOCKERFILE", "FROM scratch");
        Write(svc, "version.txt", "1.0.0");

        Assert.AreEqual(1, Detect().Services.Count);
    }

    [TestMethod]
    public void VersionFileNameMatchIsCaseSensitive()
    {
        var svc = Dir("api");
        Write(svc, "Dockerfile", "FROM scratch");
        Write(svc, "Version.txt", "1.0.0");

        // On a case-insensitive filesystem the name still compares ordinally, so no match.
        Assert.AreEqual(0, Detect().Services.Count);
    }

    [TestMethod]
    public void VersionContentNotMajorMinorPatch_YieldsServiceWithNullVersion()
    {
        var svc = Dir("api");
        Write(svc, "Dockerfile", "FROM scratch");
        Write(svc, "version.txt", "v1.2");

        var detected = Detect();

        Assert.AreEqual(1, detected.Services.Count);
        Assert.IsNull(detected.Services[0].Version);
    }

    [TestMethod]
    public void VersionContentIsTrimmed()
    {
        var svc = Dir("api");
        Write(svc, "Dockerfile", "FROM scratch");
        Write(svc, "version.txt", "  4.5.6\r\n");

        Assert.AreEqual("4.5.6", Detect().Services[0].Version);
    }

    [TestMethod]
    public void TwoServices_AreBothDetected()
    {
        foreach (var name in new[] { "api", "worker" })
        {
            var svc = Dir(name);
            Write(svc, "Dockerfile", "FROM scratch");
            Write(svc, "version.txt", "1.0.0");
        }

        var detected = Detect();

        Assert.AreEqual(2, detected.Services.Count);
        CollectionAssert.Contains(detected.Detected, "2 service(s) detected");
    }

    [TestMethod]
    public void VersionFileAtDepthThree_IsFound()
    {
        var svc = Dir("a", "b", "c");
        Write(svc, "Dockerfile", "FROM scratch");
        Write(svc, "version.txt", "1.0.0");

        Assert.AreEqual(1, Detect().Services.Count);
    }

    [TestMethod]
    public void VersionFileAtDepthFour_IsNotFound()
    {
        var svc = Dir("a", "b", "c", "d");
        Write(svc, "Dockerfile", "FROM scratch");
        Write(svc, "version.txt", "1.0.0");

        Assert.AreEqual(0, Detect().Services.Count);
    }

    [TestMethod]
    public void DotFolders_AreNotSearched()
    {
        var svc = Dir(".hidden");
        Write(svc, "Dockerfile", "FROM scratch");
        Write(svc, "version.txt", "1.0.0");

        Assert.AreEqual(0, Detect().Services.Count);
    }

    // ── Service naming ───────────────────────────────────────────────────────

    [TestMethod]
    public void ServiceName_DropsNoiseWordsAndCapitalisesFirstLetterOnly()
    {
        var svc = Dir("jattac.app.sms.gateway");
        Write(svc, "Dockerfile", "FROM scratch");
        Write(svc, "version.txt", "1.0.0");

        Assert.AreEqual("Sms Gateway", Detect().Services[0].SuggestedName);
    }

    [TestMethod]
    public void ServiceName_FallsBackToFolderNameWhenEveryWordIsFilteredOut()
    {
        var svc = Dir("web");
        Write(svc, "Dockerfile", "FROM scratch");
        Write(svc, "version.txt", "1.0.0");

        Assert.AreEqual("web", Detect().Services[0].SuggestedName);
    }

    // ── Compose parsing ──────────────────────────────────────────────────────

    [TestMethod]
    public void ComposeInRoot_MatchesServiceByFolderName_AndSetsWorkingDir()
    {
        var svc = Dir("api");
        Write(svc, "Dockerfile", "FROM scratch");
        Write(svc, "version.txt", "1.0.0");
        Write(_root, "docker-compose.yml",
            "services:\n  api:\n    image: myorg/api:1.0.0\n");

        var detected = Detect();

        Assert.AreEqual(_root, detected.WslWorkingDir);
        var s = detected.Services.Single();
        Assert.AreEqual("myorg/api", s.DockerImageName);
        Assert.IsTrue(s.ImageDetected);
        Assert.AreEqual("api", s.ComposeServiceName);
        CollectionAssert.Contains(detected.Detected, $"Docker-compose directory: {_root}");
    }

    [TestMethod]
    public void ComposeInSiblingFolder_IsDiscovered()
    {
        var sibling = Path.Combine(_sandbox, "compose-repo");
        Directory.CreateDirectory(sibling);
        Write(sibling, "docker-compose.yml", "services:\n  api:\n    image: myorg/api:1\n");
        var svc = Dir("api");
        Write(svc, "Dockerfile", "FROM scratch");
        Write(svc, "version.txt", "1.0.0");

        var detected = Detect();

        Assert.AreEqual(sibling, detected.WslWorkingDir);
        Assert.AreEqual("myorg/api", detected.Services.Single().DockerImageName);
    }

    [TestMethod]
    public void ServiceWithNoComposeMatch_IsReportedAsMissingItsImageName()
    {
        var svc = Dir("api");
        Write(svc, "Dockerfile", "FROM scratch");
        Write(svc, "version.txt", "1.0.0");

        var detected = Detect();

        var s = detected.Services.Single();
        Assert.IsNull(s.DockerImageName);
        Assert.IsFalse(s.ImageDetected);
        Assert.IsNull(s.ComposeServiceName);
        CollectionAssert.Contains(detected.Undetected, "Docker image name for service 'Api'");
    }

    [TestMethod]
    public void ComposeWithoutImageLines_DoesNotSetWorkingDir()
    {
        Write(_root, "docker-compose.yml", "services:\n  api:\n    build: .\n");

        Assert.IsNull(Detect().WslWorkingDir);
    }

    [TestMethod]
    public void ComposeImageWithRegistryPort_KeepsOnlyTheTextBeforeTheFirstColon()
    {
        // Known quirk: "localhost:5000/api:1" is read as image "localhost".
        var svc = Dir("localhost");
        Write(svc, "Dockerfile", "FROM scratch");
        Write(svc, "version.txt", "1.0.0");
        Write(_root, "docker-compose.yml",
            "services:\n  localhost:\n    image: localhost:5000/api:1\n");

        Assert.AreEqual("localhost", Detect().Services.Single().DockerImageName);
    }

    // ── Git detection ────────────────────────────────────────────────────────

    [TestMethod]
    public void GitRepoInRoot_IsDetectedWithBranchFromHead()
    {
        var git = Dir(".git");
        Write(git, "HEAD", "ref: refs/heads/main\n");

        var detected = Detect();

        Assert.AreEqual(1, detected.GitRepos.Count);
        Assert.AreEqual(_root, detected.GitRepos[0].RepoPath);
        Assert.AreEqual("main", detected.GitRepos[0].DeployBranch);
        CollectionAssert.Contains(detected.Detected, "1 git repo(s) detected");
        CollectionAssert.DoesNotContain(detected.Undetected, "Git repository path(s)");
    }

    [TestMethod]
    public void GitRepoWithoutReadableHeadBranch_DefaultsToMaster()
    {
        Dir(".git");

        Assert.AreEqual("master", Detect().GitRepos.Single().DeployBranch);
    }

    [TestMethod]
    public void DetachedHead_DefaultsToMaster()
    {
        Write(Dir(".git"), "HEAD", "3f2a9c1d0e8b7a6f5e4d3c2b1a0f9e8d7c6b5a4f\n");

        Assert.AreEqual("master", Detect().GitRepos.Single().DeployBranch);
    }

    [TestMethod]
    public void GitRepoInParentOfRoot_IsFoundByWalkingUp()
    {
        Write(Directory.CreateDirectory(Path.Combine(_sandbox, ".git")).FullName, "HEAD", "ref: refs/heads/dev\n");

        var detected = Detect();

        Assert.AreEqual(_sandbox, detected.GitRepos.Single().RepoPath);
        Assert.AreEqual("dev", detected.GitRepos.Single().DeployBranch);
    }

    [TestMethod]
    public void NestedPerServiceRepo_IsReportedAlongsideTheRootRepo()
    {
        Write(Dir(".git"), "HEAD", "ref: refs/heads/main\n");
        var svc = Dir("api");
        Write(Directory.CreateDirectory(Path.Combine(svc, ".git")).FullName, "HEAD", "ref: refs/heads/release\n");
        Write(svc, "Dockerfile", "FROM scratch");
        Write(svc, "version.txt", "1.0.0");

        var repos = Detect().GitRepos;

        Assert.AreEqual(2, repos.Count);
        Assert.IsTrue(repos.Any(r => r.RepoPath == _root && r.DeployBranch == "main"));
        Assert.IsTrue(repos.Any(r => r.RepoPath == svc && r.DeployBranch == "release"));
    }

    [TestMethod]
    public void MissingRootFolder_ReturnsEmptyResultWithoutThrowing()
    {
        var detected = ProjectDetector.Detect(Path.Combine(_sandbox, "does-not-exist"), pullLatest: false);

        Assert.AreEqual(0, detected.Services.Count);
        Assert.AreEqual(0, detected.GitRepos.Count);
    }
}
