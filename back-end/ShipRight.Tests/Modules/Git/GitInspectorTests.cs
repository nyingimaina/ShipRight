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
        public string? Compose { get; init; }

        public bool CanHandle(GitUrlInfo url) => url.Provider == "github";

        public Task<GitRepoInfo> GetRepoAsync(GitUrlInfo url, string? token, CancellationToken ct) =>
            Task.FromResult(Repo);

        public Task<IReadOnlyList<GitBranch>> GetBranchesAsync(GitUrlInfo url, string? token, CancellationToken ct) =>
            Task.FromResult(Branches);

        public Task<IReadOnlyList<GitTreeEntry>> GetTreeAsync(
            GitUrlInfo url, string reference, string? token, CancellationToken ct) => Task.FromResult(Tree);

        public Task<string?> GetFileAsync(
            GitUrlInfo url, string reference, string path, string? token, CancellationToken ct) =>
            Task.FromResult(Compose);
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
    public async Task SuggestsBuildableServicesFromTheComposeFile()
    {
        var provider = new FakeProvider
        {
            Tree = [Blob("docker-compose.yml"), Blob("Dockerfile")],
            Compose = """
                services:
                  app:
                    build: .
                    image: ${APP_TAG:-shipright/app:latest}
                  db:
                    image: mariadb:11
                """,
        };

        var result = await new GitInspector([provider]).InspectAsync("https://github.com/nyingi/shop", null);

        var app = result.Services.Single();
        Assert.AreEqual("app", app.Name);
        Assert.AreEqual("shipright/app", app.Image);
        Assert.AreEqual("Dockerfile", app.Dockerfile);
        Assert.AreEqual(".", app.Context);
        StringAssert.Contains(string.Join(" ", result.Notes).ToLowerInvariant(), "supporting");
    }

    [TestMethod]
    public async Task ReadsAnObjectBuildWithItsOwnDockerfileAndContext()
    {
        var provider = new FakeProvider
        {
            Tree = [Blob("docker-compose.yml")],
            Compose = """
                services:
                  api:
                    build:
                      context: ./api
                      dockerfile: Dockerfile.api
                    image: acme/shop-api:1.2
                """,
        };

        var result = await new GitInspector([provider]).InspectAsync("https://github.com/acme/shop", null);

        var api = result.Services.Single();
        Assert.AreEqual("api", api.Name);
        Assert.AreEqual("acme/shop-api", api.Image);
        Assert.AreEqual("Dockerfile.api", api.Dockerfile);
        Assert.AreEqual("./api", api.Context);
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
