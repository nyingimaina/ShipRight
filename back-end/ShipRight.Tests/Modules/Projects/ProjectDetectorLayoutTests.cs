using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Projects;

namespace ShipRight.Tests.Modules.Projects;

[TestClass]
public class ProjectDetectorLayoutTests : IDisposable
{
    private readonly string _root;

    public ProjectDetectorLayoutTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"sr_detect_layout_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [TestMethod]
    public void SolutionLevelDockerfile_WithVersionFileInTheProjectFolder_UsesTheSolutionFolderAsBuildContext()
    {
        var solution = Path.Combine(_root, "Lattice.Services.Foundation");
        var project = Path.Combine(solution, "Lattice.Services.Foundation");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(solution, "Dockerfile"), "FROM scratch");
        File.WriteAllText(Path.Combine(project, "version.txt"), "1.17.133");

        var detected = ProjectDetector.Detect(_root, pullLatest: false);

        Assert.AreEqual(1, detected.Services.Count);
        Assert.AreEqual(solution, detected.Services[0].BuildContextPath);
        Assert.AreEqual(Path.Combine(project, "version.txt"), detected.Services[0].VersionFilePath);
        Assert.AreEqual("1.17.133", detected.Services[0].Version);
    }
}
