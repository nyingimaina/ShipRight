using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Git;

namespace ShipRight.Tests.Modules.Git;

[TestClass]
public class GitInspectorTests
{
    private sealed class FakeProvider : IGitProvider
    {
        public string Id => "github";
        public GitRepoInfo Repo { get; init; } =
            new("github", "acme", "shop", "main", false, "https://github.com/acme/shop", 123);
        public IReadOnlyList<GitBranch> Branches { get; init; } = [];
        public IReadOnlyList<GitTreeEntry> Tree { get; init; } = [];

        public bool CanHandle(GitUrlInfo url) => url.Provider == "github";

        public Task<GitRepoInfo> GetRepoAsync(GitUrlInfo url, string? token, CancellationToken ct) =>
            Task.FromResult(Repo);

        public Task<IReadOnlyList<GitBranch>> GetBranchesAsync(GitUrlInfo url, string? token, CancellationToken ct) =>
            Task.FromResult(Branches);

        public Task<IReadOnlyList<GitTreeEntry>> GetTreeAsync(
            GitUrlInfo url, string reference, string? token, CancellationToken ct) => Task.FromResult(Tree);

        public Task<string?> GetFileAsync(
            GitUrlInfo url, string reference, string path, string? token, CancellationToken ct) =>
            Task.FromResult<string?>(null);
    }

    private static GitTreeEntry Blob(string path) => new(path, "blob", 10);

    [TestMethod]
    public async Task SuggestsTheComposeFileAndFindsDockerfiles()
    {
        var provider = new FakeProvider
        {
            Tree =
            [
                Blob("docker-compose.yml"),
                Blob("Dockerfile"),
                Blob("src/Dockerfile.api"),
                Blob("README.md"),
            ],
        };

        var result = await new GitInspector([provider]).InspectAsync("https://github.com/acme/shop", null);

        Assert.AreEqual("main", result.DefaultBranch);
        Assert.AreEqual("docker-compose.yml", result.SuggestedComposeFile);
        CollectionAssert.Contains(result.ComposeFiles.ToArray(), "docker-compose.yml");
        Assert.AreEqual(2, result.Dockerfiles.Count);
        Assert.AreEqual("acme", result.Owner);
        Assert.AreEqual("shop", result.Name);
    }

    [TestMethod]
    public async Task NotesWhenThereAreSeveralChoices()
    {
        var provider = new FakeProvider
        {
            Tree =
            [
                Blob("docker-compose.yml"),
                Blob("apps/shop/docker-compose.yml"),
                Blob("Dockerfile"),
                Blob("Dockerfile.api"),
            ],
        };

        var result = await new GitInspector([provider]).InspectAsync("https://github.com/acme/shop", null);

        var notes = string.Join(" ", result.Notes).ToLowerInvariant();
        StringAssert.Contains(notes, "more than one compose");
        StringAssert.Contains(notes, "more than one dockerfile");
    }

    [TestMethod]
    public async Task NotesWhenThereIsNoComposeFile()
    {
        var provider = new FakeProvider { Tree = [Blob("README.md")] };

        var result = await new GitInspector([provider]).InspectAsync("https://github.com/acme/shop", null);

        Assert.IsNull(result.SuggestedComposeFile);
        StringAssert.Contains(string.Join(" ", result.Notes).ToLowerInvariant(), "no compose file");
    }

    [TestMethod]
    public async Task RefusesAnotherHost()
    {
        var inspector = new GitInspector([new FakeProvider()]);

        await Assert.ThrowsExceptionAsync<NotSupportedException>(
            () => inspector.InspectAsync("https://gitlab.com/acme/shop", null));
    }

    [TestMethod]
    public async Task RefusesGarbage()
    {
        var inspector = new GitInspector([new FakeProvider()]);

        await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => inspector.InspectAsync("not a repository", null));
    }
}
