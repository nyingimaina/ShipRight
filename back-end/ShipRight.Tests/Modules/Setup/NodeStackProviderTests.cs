using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Setup;

namespace ShipRight.Tests.Modules.Setup;

[TestClass]
public class NodeStackProviderTests
{
    private static IReadOnlyList<DetectedComponent> Detect(TempTree t) =>
        new NodeStackProvider().Detect(t.Root).Components;

    private static string Pkg(string deps = "", string scripts = "", string extra = "") =>
        "{ \"name\": \"web\"" +
        (deps.Length > 0 ? ", \"dependencies\": { " + deps + " }" : "") +
        (scripts.Length > 0 ? ", \"scripts\": { " + scripts + " }" : "") +
        (extra.Length > 0 ? ", " + extra : "") + " }";

    // ── Detection ────────────────────────────────────────────────────────────

    [TestMethod]
    public void NextJsApp_IsDetectedOnPort3000()
    {
        using var t = new TempTree();
        t.Add("web/package.json", Pkg("\"next\": \"15.0.0\", \"react\": \"19.0.0\"", "\"build\": \"next build\", \"start\": \"next start\""));

        var c = Detect(t).Single();

        Assert.AreEqual("node", c.StackId);
        Assert.AreEqual("nextjs", c.Kind);
        Assert.AreEqual(3000, c.Port);
        Assert.AreEqual(Confidence.High, c.Confidence);
        Assert.AreEqual(t.Full("web"), c.Directory);
        Assert.AreEqual(t.Full("web"), c.ContextDirectory);
    }

    [TestMethod]
    public void ViteApp_IsDetectedAsStaticSiteServedOnPort80()
    {
        using var t = new TempTree();
        t.Add("ui/package.json", Pkg("\"react\": \"19.0.0\"", "\"build\": \"vite build\"", "\"devDependencies\": { \"vite\": \"6.0.0\" }"));

        var c = Detect(t).Single();

        Assert.AreEqual("spa", c.Kind);
        Assert.AreEqual(80, c.Port);
        Assert.AreEqual("dist", c.Facts["outputDir"]);
    }

    [TestMethod]
    public void CreateReactApp_BuildsToTheBuildFolder()
    {
        using var t = new TempTree();
        t.Add("ui/package.json", Pkg("\"react-scripts\": \"5.0.1\"", "\"build\": \"react-scripts build\""));

        Assert.AreEqual("build", Detect(t).Single().Facts["outputDir"]);
    }

    [TestMethod]
    public void AngularApp_IsDetectedButFlaggedBecauseItsOutputFolderIsNotKnown()
    {
        using var t = new TempTree();
        t.Add("ui/package.json", Pkg("\"@angular/core\": \"19.0.0\"", "\"build\": \"ng build\"", "\"devDependencies\": { \"@angular/cli\": \"19.0.0\" }"));

        var result = new NodeStackProvider().Detect(t.Root);

        var c = result.Components.Single();
        Assert.AreEqual("spa", c.Kind);
        Assert.AreEqual(Confidence.Medium, c.Confidence);
        Assert.IsTrue(c.Notes.Any(n => n.Contains("output")));
    }

    [TestMethod]
    public void ExpressApi_IsDetectedAsNodeServer()
    {
        using var t = new TempTree();
        t.Add("api/package.json", Pkg("\"express\": \"5.0.0\"", "\"start\": \"node server.js\""));

        var c = Detect(t).Single();

        Assert.AreEqual("node-server", c.Kind);
        Assert.AreEqual(3000, c.Port);
        Assert.AreEqual(Confidence.High, c.Confidence);
    }

    [TestMethod]
    public void ServerFrameworkWinsOverAFrontEndBuildTool()
    {
        using var t = new TempTree();
        t.Add("app/package.json", Pkg("\"express\": \"5.0.0\"", "\"start\": \"node server.js\"", "\"devDependencies\": { \"vite\": \"6.0.0\" }"));

        Assert.AreEqual("node-server", Detect(t).Single().Kind);
    }

    [TestMethod]
    public void UnknownPackageWithAStartScript_IsALowConfidenceNodeServer()
    {
        using var t = new TempTree();
        t.Add("svc/package.json", Pkg("", "\"start\": \"node index.js\""));

        var c = Detect(t).Single();

        Assert.AreEqual("node-server", c.Kind);
        Assert.AreEqual(Confidence.Low, c.Confidence);
    }

    [TestMethod]
    public void LibraryWithNoStartScriptOrFramework_IsNotAComponent()
    {
        using var t = new TempTree();
        t.Add("lib/package.json", Pkg("\"lodash\": \"4.0.0\"", "\"build\": \"tsc\""));

        Assert.AreEqual(0, Detect(t).Count);
    }

    [TestMethod]
    public void WorkspaceRoot_IsSkippedButItsPackagesAreFound()
    {
        using var t = new TempTree();
        t.Add("package.json", "{ \"name\": \"mono\", \"private\": true, \"workspaces\": [\"apps/*\"], \"scripts\": { \"start\": \"turbo run start\" } }");
        t.Add("apps/site/package.json", Pkg("\"next\": \"15.0.0\"", "\"build\": \"next build\""));

        var c = Detect(t).Single();

        Assert.AreEqual("nextjs", c.Kind);
        Assert.AreEqual(t.Full("apps/site"), c.Directory);
    }

    [TestMethod]
    public void NodeModulesAndDotFolders_AreNotSearched()
    {
        using var t = new TempTree();
        t.Add("web/node_modules/dep/package.json", Pkg("\"next\": \"15.0.0\""));
        t.Add(".cache/package.json", Pkg("\"next\": \"15.0.0\""));

        Assert.AreEqual(0, Detect(t).Count);
    }

    [TestMethod]
    public void UnparseablePackageJson_IsSkippedWithAWarning()
    {
        using var t = new TempTree();
        t.Add("web/package.json", "{ not json");

        var result = new NodeStackProvider().Detect(t.Root);

        Assert.AreEqual(0, result.Components.Count);
        Assert.IsTrue(result.Warnings.Any(w => w.Contains("package.json")));
    }

    [TestMethod]
    public void PackageNameBecomesTheComponentName_WithTheScopeRemoved()
    {
        using var t = new TempTree();
        t.Add("web/package.json", "{ \"name\": \"@acme/storefront\", \"dependencies\": { \"next\": \"15.0.0\" } }");

        Assert.AreEqual("storefront", Detect(t).Single().Name);
    }

    [TestMethod]
    public void MissingPackageName_FallsBackToTheFolderName()
    {
        using var t = new TempTree();
        t.Add("web/package.json", "{ \"dependencies\": { \"next\": \"15.0.0\" } }");

        Assert.AreEqual("web", Detect(t).Single().Name);
    }

    [TestMethod]
    public void DeclaredVersion_IsNormalisedToMajorMinorPatch()
    {
        using var t = new TempTree();
        t.Add("web/package.json", Pkg("\"next\": \"15.0.0\"", "", "\"version\": \"1.7.3-rc.2\""));

        Assert.AreEqual("1.7.3", Detect(t).Single().Version);
    }

    // ── Package manager and Node version ─────────────────────────────────────

    [TestMethod]
    [DataRow("package-lock.json", "npm ci")]
    [DataRow("yarn.lock", "yarn install --frozen-lockfile")]
    [DataRow("pnpm-lock.yaml", "pnpm install --frozen-lockfile")]
    public void LockFile_DecidesThePackageManagerAndInstallCommand(string lockFile, string install)
    {
        using var t = new TempTree();
        t.Add("web/package.json", Pkg("\"next\": \"15.0.0\""));
        t.Add("web/" + lockFile, "");

        var c = Detect(t).Single();

        Assert.AreEqual(install, c.Facts["installCommand"]);
        Assert.AreEqual(lockFile, c.Facts["lockFile"]);
    }

    [TestMethod]
    public void NoLockFile_FallsBackToNpmInstall()
    {
        using var t = new TempTree();
        t.Add("web/package.json", Pkg("\"next\": \"15.0.0\""));

        var c = Detect(t).Single();

        Assert.AreEqual("npm install", c.Facts["installCommand"]);
        Assert.AreEqual("", c.Facts["lockFile"]);
    }

    [TestMethod]
    [DataRow(">=20.0.0", "20")]
    [DataRow("^22.1.0", "22")]
    [DataRow("18.x", "18")]
    public void EnginesField_DrivesTheNodeVersion(string engines, string expected)
    {
        using var t = new TempTree();
        t.Add("web/package.json", Pkg("\"next\": \"15.0.0\"", "", "\"engines\": { \"node\": \"" + engines + "\" }"));

        Assert.AreEqual(expected, Detect(t).Single().Facts["nodeVersion"]);
    }

    [TestMethod]
    public void NvmrcIsUsedWhenEnginesIsAbsent()
    {
        using var t = new TempTree();
        t.Add("web/package.json", Pkg("\"next\": \"15.0.0\""));
        t.Add("web/.nvmrc", "v22.3.0\n");

        Assert.AreEqual("22", Detect(t).Single().Facts["nodeVersion"]);
    }

    [TestMethod]
    public void NoNodeVersionHint_DefaultsToTheCurrentLts()
    {
        using var t = new TempTree();
        t.Add("web/package.json", Pkg("\"next\": \"15.0.0\""));

        Assert.AreEqual("24", Detect(t).Single().Facts["nodeVersion"]);
    }

    // ── Rendering ────────────────────────────────────────────────────────────

    private static string Render(TempTree t) =>
        new NodeStackProvider().RenderDockerfile(new NodeStackProvider().Detect(t.Root).Components.Single());

    [TestMethod]
    public void RenderedDockerfile_ForANodeServer_MatchesTheGolden()
    {
        using var t = new TempTree();
        t.Add("package.json", Pkg("\"express\": \"5.0.0\"", "\"build\": \"tsc\", \"start\": \"node dist/server.js\""));
        t.Add("package-lock.json", "{}");

        var expected = string.Join("\n",
            "# Generated by ShipRight. Safe to edit - ShipRight never overwrites an existing Dockerfile.",
            "FROM node:24-slim AS build",
            "WORKDIR /app",
            "COPY package.json package-lock.json ./",
            "RUN npm ci",
            "COPY . .",
            "RUN npm run build",
            "RUN npm prune --omit=dev",
            "",
            "FROM node:24-slim AS final",
            "WORKDIR /app",
            "ENV NODE_ENV=production",
            "COPY --from=build --chown=node:node /app ./",
            "USER node",
            "EXPOSE 3000",
            "CMD [\"npm\", \"start\"]",
            "");
        Assert.AreEqual(expected, Render(t));
    }

    [TestMethod]
    public void RenderedDockerfile_ForNodeServerWithoutABuildScript_SkipsTheBuildStep()
    {
        using var t = new TempTree();
        t.Add("package.json", Pkg("\"express\": \"5.0.0\"", "\"start\": \"node server.js\""));

        Assert.IsFalse(Render(t).Contains("npm run build"));
    }

    [TestMethod]
    public void RenderedDockerfile_ForASpa_MatchesTheGolden()
    {
        using var t = new TempTree();
        t.Add("package.json", Pkg("\"react\": \"19.0.0\"", "\"build\": \"vite build\"", "\"devDependencies\": { \"vite\": \"6.0.0\" }"));
        t.Add("package-lock.json", "{}");

        var expected = string.Join("\n",
            "# Generated by ShipRight. Safe to edit - ShipRight never overwrites an existing Dockerfile.",
            "FROM node:24-slim AS build",
            "WORKDIR /app",
            "COPY package.json package-lock.json ./",
            "RUN npm ci",
            "COPY . .",
            "RUN npm run build",
            "",
            "FROM nginx:alpine AS final",
            "RUN printf 'server {\\n  listen 80;\\n  root /usr/share/nginx/html;\\n  location / {\\n    try_files $uri $uri/ /index.html;\\n  }\\n}\\n' > /etc/nginx/conf.d/default.conf",
            "COPY --from=build /app/dist /usr/share/nginx/html",
            "EXPOSE 80",
            "");
        Assert.AreEqual(expected, Render(t));
    }

    [TestMethod]
    public void RenderedDockerfile_ForYarn_EnablesCorepackAndUsesYarnToBuild()
    {
        using var t = new TempTree();
        t.Add("package.json", Pkg("\"next\": \"15.0.0\"", "\"build\": \"next build\", \"start\": \"next start\""));
        t.Add("yarn.lock", "");

        var text = Render(t);

        StringAssert.Contains(text, "RUN corepack enable");
        StringAssert.Contains(text, "RUN yarn install --frozen-lockfile");
        StringAssert.Contains(text, "RUN yarn build");
        StringAssert.Contains(text, "COPY package.json yarn.lock ./");
    }

    [TestMethod]
    public void RenderedDockerfile_WithoutALockFile_CopiesOnlyPackageJson()
    {
        using var t = new TempTree();
        t.Add("package.json", Pkg("\"next\": \"15.0.0\"", "\"build\": \"next build\""));

        var text = Render(t);

        StringAssert.Contains(text, "COPY package.json ./");
        StringAssert.Contains(text, "RUN npm install");
    }

    [TestMethod]
    public void RenderedDockerfile_UsesTheDetectedNodeVersion()
    {
        using var t = new TempTree();
        t.Add("package.json", Pkg("\"next\": \"15.0.0\"", "", "\"engines\": { \"node\": \">=20\" }"));

        StringAssert.Contains(Render(t), "FROM node:20-slim AS build");
    }

    [TestMethod]
    public void RenderedDockerfile_ForAServerWithOnlyAMainField_RunsThatFile()
    {
        using var t = new TempTree();
        t.Add("package.json", "{ \"name\": \"svc\", \"main\": \"src/index.js\", \"dependencies\": { \"express\": \"5.0.0\" } }");

        StringAssert.Contains(Render(t), "CMD [\"node\", \"src/index.js\"]");
    }

    [TestMethod]
    public void RenderedDockerIgnore_KeepsDependenciesAndSecretsOutOfTheImage()
    {
        using var t = new TempTree();
        t.Add("package.json", Pkg("\"next\": \"15.0.0\""));
        var provider = new NodeStackProvider();

        var text = provider.RenderDockerIgnore(provider.Detect(t.Root).Components.Single());

        foreach (var entry in new[] { "node_modules", ".git", ".env", ".next" })
            StringAssert.Contains(text, entry);
    }
}
