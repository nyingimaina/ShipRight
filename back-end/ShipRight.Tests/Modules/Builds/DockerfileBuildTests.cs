using System.IO;
using System.Linq;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ShipRight.Tests.Modules.Builds;

[TestClass]
public class DockerfileBuildTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ShipRight.sln")))
            dir = dir.Parent;
        Assert.IsNotNull(dir, "Could not locate repo root from test output directory.");
        return dir.FullName;
    }

    private static string DockerfilePath => Path.Combine(RepoRoot, "Dockerfile");

    private static string[] ServerProjectReferences()
    {
        var csproj = Path.Combine(RepoRoot, "back-end", "ShipRight.Server", "ShipRight.Server.csproj");
        var doc = XDocument.Load(csproj);
        var ns = doc.Root!.Name.NamespaceName;

        return doc.Descendants(XName.Get("ProjectReference", ns))
            .Select(r => (string?)r.Attribute("Include"))
            .Where(i => !string.IsNullOrEmpty(i))
            .Select(i => Path.GetFileName(Path.GetDirectoryName(i!.Replace('\\', '/'))))
            .Where(s => !string.IsNullOrEmpty(s))
            .Cast<string>()
            .ToArray();
    }

    private static string[] DockerfileBackendCopiedProjectDirs()
    {
        var lines = File.ReadAllLines(DockerfilePath);

        // Start of backend stage: FROM sdk image
        var backendStart = Array.FindIndex(lines, l => l.StartsWith("FROM mcr.microsoft.com/dotnet/sdk:"));
        Assert.AreNotEqual(-1, backendStart, "No backend stage (FROM ...dotnet/sdk:...) found in Dockerfile.");

        var backendLines = lines.Skip(backendStart).ToArray();
        return backendLines
            .Where(l => l.StartsWith("COPY "))
            .SelectMany(l => l.Split(' ', 3))
            .Where(p => p.StartsWith("back-end/"))
            .Select(p => p.TrimEnd('/').Replace('\\', '/'))
            .Select(p => p.Split('/')[1])
            .Distinct()
            .ToArray();
    }

    [TestMethod]
    public void Dockerfile_BackendStageCopiesEveryProjectReferencedByServer()
    {
        var copied = DockerfileBackendCopiedProjectDirs();
        var missing = ServerProjectReferences()
            .Where(r => !copied.Contains(r, StringComparer.OrdinalIgnoreCase))
            .ToArray();

        Assert.AreEqual(0, missing.Length,
            $"Dockerfile backend stage does not COPY these referenced project(s): {string.Join(", ", missing)}");
    }

    [TestMethod]
    public void Dockerfile_UsesNet10LtsBaseImages()
    {
        var text = File.ReadAllText(DockerfilePath);
        StringAssert.Contains(text, "mcr.microsoft.com/dotnet/sdk:10.0");
        StringAssert.Contains(text, "mcr.microsoft.com/dotnet/aspnet:10.0-alpine");
    }

    [TestMethod]
    public void AllBackendProjects_TargetNet10Lts()
    {
        var csprojFiles = Directory.GetFiles(Path.Combine(RepoRoot, "back-end"), "*.csproj", SearchOption.AllDirectories);
        Assert.IsTrue(csprojFiles.Length >= 4, "Expected at least the four backend projects.");

        var nonLts = new System.Collections.Generic.List<string>();
        foreach (var file in csprojFiles)
        {
            var text = File.ReadAllText(file);
            if (!text.Contains("<TargetFramework>net10.0"))
                nonLts.Add(file);
        }

        Assert.AreEqual(0, nonLts.Count,
            $"Project(s) not targeting net10.0 LTS: {string.Join(", ", nonLts)}");
    }
}