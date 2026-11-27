using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Platform;
using ShipRight.Modules.Projects;

namespace ShipRight.Tests.Modules.Platform;

[TestClass]
public class ProjectSourceTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sr-src-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    [TestMethod]
    public void FromProject_PrefersTheExplicitSource()
    {
        var project = new ProjectConfig
        {
            Source = new ProjectSource(ProjectSourceKind.GitRepository, "git@github.com:acme/shop.git", "main", "apps/shop"),
            GitRepos = [new GitConfig { RepoPath = "C:/local/shop" }],
        };

        var source = ProjectSources.FromProject(project);

        Assert.IsNotNull(source);
        Assert.AreEqual(ProjectSourceKind.GitRepository, source!.Kind);
        Assert.AreEqual("apps/shop", source.PathPrefix);
    }

    [TestMethod]
    public void FromProject_FallsBackToTheFirstGitRepo()
    {
        var project = new ProjectConfig
        {
            GitRepos = [new GitConfig { RepoPath = "C:/local/shop" }],
            Wsl = new WslConfig { WorkingDir = "/home/me/shop" },
        };

        var source = ProjectSources.FromProject(project);

        Assert.IsNotNull(source);
        Assert.AreEqual(ProjectSourceKind.LocalFolder, source!.Kind);
        Assert.AreEqual("C:/local/shop", source.Location);
    }

    [TestMethod]
    public void FromProject_FallsBackToTheWorkingDir()
    {
        var project = new ProjectConfig { Wsl = new WslConfig { WorkingDir = "/home/me/shop" } };

        var source = ProjectSources.FromProject(project);

        Assert.IsNotNull(source);
        Assert.AreEqual("/home/me/shop", source!.Location);
    }

    [TestMethod]
    public void FromProject_ReturnsNullWhenNothingIsKnown()
    {
        Assert.IsNull(ProjectSources.FromProject(new ProjectConfig()));
    }

    [TestMethod]
    public async Task LocalFolderTree_ListsAndReadsFiles()
    {
        var dir = TempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "src"));
            await File.WriteAllTextAsync(Path.Combine(dir, "src", "app.txt"), "hello");
            await File.WriteAllTextAsync(Path.Combine(dir, "README.md"), "# shop");

            var tree = new LocalFolderSourceTree(new ProjectSource(ProjectSourceKind.LocalFolder, dir));

            Assert.IsTrue(await tree.ExistsAsync());
            CollectionAssert.AreEquivalent(new[] { "README.md" }, (await tree.ListFilesAsync()).ToArray());
            CollectionAssert.AreEquivalent(new[] { "src/app.txt" }, (await tree.ListFilesAsync("src")).ToArray());
            Assert.AreEqual("hello", await tree.ReadTextAsync("src/app.txt"));
            Assert.IsNull(await tree.ReadTextAsync("src/missing.txt"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public async Task LocalFolderTree_RefusesToLeaveTheFolder()
    {
        var dir = TempDir();
        try
        {
            var tree = new LocalFolderSourceTree(new ProjectSource(ProjectSourceKind.LocalFolder, dir));

            await Assert.ThrowsExceptionAsync<ArgumentException>(() => tree.ReadTextAsync("../secret.txt"));
            await Assert.ThrowsExceptionAsync<ArgumentException>(() => tree.ListFilesAsync("/"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public async Task GitRepositoryTree_KeepsMetadataButHasNoLocalFiles()
    {
        var source = new ProjectSource(
            ProjectSourceKind.GitRepository, "git@github.com:acme/shop.git", "main", "apps/shop", "12345");

        var tree = new GitRepositorySourceTree(source);

        Assert.AreEqual(source, tree.Source);
        Assert.IsTrue(await tree.ExistsAsync());
        await Assert.ThrowsExceptionAsync<NotSupportedException>(() => tree.ListFilesAsync());
        await Assert.ThrowsExceptionAsync<NotSupportedException>(() => tree.ReadTextAsync("README.md"));
    }
}
