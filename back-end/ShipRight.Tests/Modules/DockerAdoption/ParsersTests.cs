using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.DockerAdoption;

namespace ShipRight.Tests.Modules.DockerAdoption;

[TestClass]
public class DockerfileParserTests
{
    [TestMethod]
    public void Stages_AreNamed_AndTheUnreferencedOnesAreTheEnds()
    {
        var d = DockerfileParser.Parse("""
            FROM node:20 AS deps
            FROM deps AS build
            COPY --from=deps /x /y
            FROM node:20-alpine AS runner
            COPY --from=build /app/out ./
            """);
        CollectionAssert.AreEqual(new[] { "deps", "build", "runner" }, d.Stages.ToArray());
        Assert.AreEqual("runner", d.FinalStage);
        Assert.AreEqual(0, d.OtherEnds.Count);
    }

    [TestMethod]
    public void AnExtraEndStage_IsOfferedAsAnotherImage()
    {
        var d = DockerfileParser.Parse("FROM a AS base\nFROM base AS api\nFROM base AS worker\n");
        Assert.AreEqual("worker", d.FinalStage);
        CollectionAssert.AreEqual(new[] { "api" }, d.OtherEnds.ToArray());
    }

    [TestMethod]
    public void AnUnnamedFinalStage_HasNoName()
    {
        var d = DockerfileParser.Parse("FROM a AS build\nFROM b\nCOPY --from=build /o /o\n");
        Assert.IsNull(d.FinalStage);
        Assert.AreEqual(0, d.OtherEnds.Count);
    }

    [TestMethod]
    public void CopySources_AreReadInEveryForm()
    {
        var d = DockerfileParser.Parse("""
            FROM x
            COPY back-end/company-man.csproj back-end/
            COPY --chown=1000:1000 a.txt b/c.txt /dest/
            copy ["with space.txt", "/dest/"]
            COPY front-end/package.json front-end/package-lock.json* ./
            """);
        CollectionAssert.AreEqual(
            new[] { "back-end/company-man.csproj", "a.txt", "b/c.txt", "with space.txt", "front-end/package.json", "front-end/package-lock.json*" },
            d.CopySources.ToArray());
        Assert.IsFalse(d.CopiesEverything);
    }

    [TestMethod]
    public void CopiesFromAnotherStage_OrAUrl_AreNotBuildContextSources()
    {
        var d = DockerfileParser.Parse("FROM a AS build\nFROM b\nCOPY --from=build /o /o\nCOPY --from=nginx:1 /etc /etc\nADD https://x.example/y.tgz /\nADD local.tgz /\n");
        CollectionAssert.AreEqual(new[] { "local.tgz" }, d.CopySources.ToArray());
    }

    [TestMethod]
    public void CopyingEverything_IsRecognised()
    {
        Assert.IsTrue(DockerfileParser.Parse("FROM x\nCOPY . ./\n").CopiesEverything);
        Assert.IsTrue(DockerfileParser.Parse("FROM x\nCOPY ./ /app\n").CopiesEverything);
        Assert.IsTrue(DockerfileParser.Parse("FROM x\nADD . /app\n").CopiesEverything);
        Assert.IsFalse(DockerfileParser.Parse("FROM x\nCOPY ./src /app\n").CopiesEverything);
    }

    [TestMethod]
    public void LineContinuations_CommentsAndBlankLines_AreHandled()
    {
        var d = DockerfileParser.Parse("# escape=\\\nFROM x\n\n# COPY not/a/source /x\nCOPY a.txt \\\n     b.txt \\\n     /dest/\n");
        CollectionAssert.AreEqual(new[] { "a.txt", "b.txt" }, d.CopySources.ToArray());
    }

    [TestMethod]
    public void Arguments_AreListedWithWhetherTheyHaveADefault()
    {
        var d = DockerfileParser.Parse("ARG BASE=node:20\nFROM $BASE\nARG API_URL\nARG MODE=prod\n");
        Assert.AreEqual(3, d.Args.Count);
        Assert.IsTrue(d.Args.Single(a => a.Name == "BASE").HasDefault);
        Assert.IsFalse(d.Args.Single(a => a.Name == "API_URL").HasDefault);
        Assert.IsTrue(d.Args.Single(a => a.Name == "MODE").HasDefault);
    }

    [TestMethod]
    public void HereDocsAndEmptyFiles_DoNotBreakIt()
    {
        Assert.AreEqual(0, DockerfileParser.Parse("").CopySources.Count);
        var d = DockerfileParser.Parse("FROM x\nCOPY <<EOF /etc/x\nhello\nEOF\nCOPY real.txt /r\n");
        CollectionAssert.AreEqual(new[] { "real.txt" }, d.CopySources.ToArray());
    }

    [TestMethod]
    public void WindowsLineEndings_Work()
    {
        var d = DockerfileParser.Parse("FROM x AS a\r\nCOPY one.txt /o\r\n");
        CollectionAssert.AreEqual(new[] { "one.txt" }, d.CopySources.ToArray());
        CollectionAssert.AreEqual(new[] { "a" }, d.Stages.ToArray());
    }
}

[TestClass]
public class ComposeLiteTests
{
    private const string Savanna = """
        x-logging: &default-logging
          driver: json-file
          options:
            max-size: "10m"

        services:
          nginx-proxy:
            image: nginxproxy/nginx-proxy
            restart: always
            logging: *default-logging
            ports:
              - "80:80"
          db:
            image: mariadb:11.4
            command: >
              --innodb-buffer-pool-size=384M
              --max-connections=50
            healthcheck:
              test: ["CMD", "mariadb-admin", "ping"]
          backend:
            image: 825347768469.dkr.ecr.eu-north-1.amazonaws.com/savanna-herdiq-backend:${IMAGE_TAG:-latest}
            restart: always
            environment:
              - ASPNETCORE_ENVIRONMENT=Production
            depends_on:
              db:
                condition: service_healthy
              minio:
                condition: service_healthy
          frontend:
            image: "825347768469.dkr.ecr.eu-north-1.amazonaws.com/savanna-herdiq-frontend:${IMAGE_TAG:-latest}"  # the site
            depends_on:
              - backend
        volumes:
          db-data:
        """;

    [TestMethod]
    public void TheServicesAreFound_InOrder_WithTheirImages()
    {
        var s = ComposeLite.Parse(Savanna);
        CollectionAssert.AreEqual(new[] { "nginx-proxy", "db", "backend", "frontend" }, s.Select(x => x.Name).ToArray());
        Assert.AreEqual("nginxproxy/nginx-proxy", s[0].Image);
        Assert.AreEqual("825347768469.dkr.ecr.eu-north-1.amazonaws.com/savanna-herdiq-backend:${IMAGE_TAG:-latest}", s[2].Image);
        Assert.AreEqual("825347768469.dkr.ecr.eu-north-1.amazonaws.com/savanna-herdiq-frontend:${IMAGE_TAG:-latest}", s[3].Image, "quotes and a trailing comment are removed");
    }

    [TestMethod]
    public void DependsOn_IsReadAsAListOrAMap()
    {
        var s = ComposeLite.Parse(Savanna);
        CollectionAssert.AreEqual(new[] { "db", "minio" }, s.Single(x => x.Name == "backend").DependsOn.ToArray());
        CollectionAssert.AreEqual(new[] { "backend" }, s.Single(x => x.Name == "frontend").DependsOn.ToArray());
    }

    [TestMethod]
    public void ABuildGivenAsAString_IsTheContext()
    {
        var s = ComposeLite.Parse("services:\n  api:\n    build: ./api\n    image: shop/api\n");
        Assert.AreEqual("./api", s.Single().Build!.Context);
        Assert.IsNull(s.Single().Build!.Dockerfile);
    }

    [TestMethod]
    public void ABuildGivenAsAMap_HasContextDockerfileTargetAndArguments()
    {
        var s = ComposeLite.Parse("""
            services:
              api:
                image: shop/api
                build:
                  context: ./api
                  dockerfile: Dockerfile.prod
                  target: runner
                  args:
                    NODE_ENV: production
                    API_URL: "https://x.example"
              web:
                build:
                  context: .
                  args:
                    - MODE=dev
                    - FLAG
            """);
        var api = s[0].Build!;
        Assert.AreEqual("./api", api.Context);
        Assert.AreEqual("Dockerfile.prod", api.Dockerfile);
        Assert.AreEqual("runner", api.Target);
        Assert.AreEqual("production", api.Args["NODE_ENV"]);
        Assert.AreEqual("https://x.example", api.Args["API_URL"]);
        var web = s[1].Build!;
        Assert.AreEqual("dev", web.Args["MODE"]);
        Assert.AreEqual("", web.Args["FLAG"]);
    }

    [TestMethod]
    public void NoServices_OrJunk_IsAnEmptyList_NeverAnError()
    {
        Assert.AreEqual(0, ComposeLite.Parse("").Count);
        Assert.AreEqual(0, ComposeLite.Parse("version: '3'\nvolumes:\n  a:\n").Count);
        Assert.AreEqual(0, ComposeLite.Parse("{{{ not yaml").Count);
    }

    [TestMethod]
    public void WindowsLineEndingsAndTabsOfCommentsAreFine()
    {
        var s = ComposeLite.Parse("services:\r\n  # the api\r\n  api:\r\n    image: shop/api # tag below\r\n");
        Assert.AreEqual("shop/api", s.Single().Image);
    }
}
