using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Setup;

namespace ShipRight.Tests.Modules.Setup;

[TestClass]
public class DotNetStackProviderTests
{
    private const string WebCsproj =
        "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>";

    private static IReadOnlyList<DetectedComponent> Detect(TempTree t) =>
        new DotNetStackProvider().Detect(t.Root).Components;

    [TestMethod]
    public void WebProject_IsDetectedAsAspNetWebOnPort8080()
    {
        using var t = new TempTree();
        t.Add("Api/Api.csproj", WebCsproj);

        var c = Detect(t).Single();

        Assert.AreEqual("dotnet", c.StackId);
        Assert.AreEqual("aspnet-web", c.Kind);
        Assert.AreEqual("Api", c.Name);
        Assert.AreEqual(8080, c.Port);
        Assert.AreEqual(Confidence.High, c.Confidence);
        Assert.AreEqual(t.Full("Api"), c.Directory);
        Assert.AreEqual(t.Full("Api"), c.ContextDirectory);
    }

    [TestMethod]
    public void WorkerProject_IsDetectedWithoutAPort()
    {
        using var t = new TempTree();
        t.Add("Jobs/Jobs.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk.Worker\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");

        var c = Detect(t).Single();

        Assert.AreEqual("dotnet-worker", c.Kind);
        Assert.IsNull(c.Port);
    }

    [TestMethod]
    public void ConsoleExe_IsDetectedWithMediumConfidence()
    {
        using var t = new TempTree();
        t.Add("Tool/Tool.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");

        var c = Detect(t).Single();

        Assert.AreEqual("dotnet-worker", c.Kind);
        Assert.AreEqual(Confidence.Medium, c.Confidence);
    }

    [TestMethod]
    public void ClassLibrary_IsNotAComponent()
    {
        using var t = new TempTree();
        t.Add("Lib/Lib.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");

        Assert.AreEqual(0, Detect(t).Count);
    }

    [TestMethod]
    public void TestProject_IsSkippedEvenWhenItIsAnExe()
    {
        using var t = new TempTree();
        t.Add("Api.Tests/Api.Tests.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup>" +
            "<ItemGroup><PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"17.0.0\" /></ItemGroup></Project>");

        Assert.AreEqual(0, Detect(t).Count);
    }

    [TestMethod]
    public void WindowsDesktopApp_IsSkipped()
    {
        using var t = new TempTree();
        t.Add("Desk/Desk.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>WinExe</OutputType><TargetFramework>net10.0-windows</TargetFramework></PropertyGroup></Project>");

        Assert.AreEqual(0, Detect(t).Count);
    }

    [TestMethod]
    public void LegacyFrameworkProject_IsSkippedWithAWarning()
    {
        using var t = new TempTree();
        t.Add("Old/Old.csproj",
            "<Project ToolsVersion=\"15.0\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup></Project>");

        var result = new DotNetStackProvider().Detect(t.Root);

        Assert.AreEqual(0, result.Components.Count);
        Assert.IsTrue(result.Warnings.Any(w => w.Contains("Old.csproj") && w.Contains(".NET Framework")));
    }

    [TestMethod]
    public void UnparseableProjectFile_IsSkippedWithAWarning()
    {
        using var t = new TempTree();
        t.Add("Bad/Bad.csproj", "<Project><oops");

        var result = new DotNetStackProvider().Detect(t.Root);

        Assert.AreEqual(0, result.Components.Count);
        Assert.IsTrue(result.Warnings.Any(w => w.Contains("Bad.csproj")));
    }

    [TestMethod]
    public void BinObjAndDotFolders_AreNotSearched()
    {
        using var t = new TempTree();
        t.Add("Api/bin/Debug/Copy.csproj", WebCsproj);
        t.Add("Api/obj/Copy2.csproj", WebCsproj);
        t.Add(".hidden/Hid.csproj", WebCsproj);

        Assert.AreEqual(0, Detect(t).Count);
    }

    [TestMethod]
    public void TargetFramework_DrivesTheImageVersion()
    {
        using var t = new TempTree();
        t.Add("Api/Api.csproj", WebCsproj.Replace("net10.0", "net8.0"));

        Assert.AreEqual("8.0", Detect(t).Single().Facts["dotnetVersion"]);
    }

    [TestMethod]
    public void MultiTargeting_PicksTheHighestFramework()
    {
        using var t = new TempTree();
        t.Add("Api/Api.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><TargetFrameworks>net8.0;net9.0</TargetFrameworks></PropertyGroup></Project>");

        Assert.AreEqual("9.0", Detect(t).Single().Facts["dotnetVersion"]);
    }

    [TestMethod]
    public void MissingTargetFramework_DefaultsToTheCurrentLts()
    {
        using var t = new TempTree();
        t.Add("Api/Api.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\"></Project>");

        Assert.AreEqual("10.0", Detect(t).Single().Facts["dotnetVersion"]);
    }

    [TestMethod]
    public void AssemblyName_FallsBackToTheProjectFileName_AndHonoursAnOverride()
    {
        using var t = new TempTree();
        t.Add("A/A.csproj", WebCsproj);
        t.Add("B/B.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyName>Custom.Name</AssemblyName></PropertyGroup></Project>");

        var all = Detect(t).ToDictionary(c => c.Name);

        Assert.AreEqual("A", all["A"].Facts["assemblyName"]);
        Assert.AreEqual("Custom.Name", all["B"].Facts["assemblyName"]);
    }

    [TestMethod]
    public void DeclaredVersion_IsNormalisedToMajorMinorPatch()
    {
        using var t = new TempTree();
        t.Add("Api/Api.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><Version>2.4-beta.1</Version></PropertyGroup></Project>");

        Assert.AreEqual("2.4.0", Detect(t).Single().Version);
    }

    [TestMethod]
    public void NoDeclaredVersion_LeavesVersionNull()
    {
        using var t = new TempTree();
        t.Add("Api/Api.csproj", WebCsproj);

        Assert.IsNull(Detect(t).Single().Version);
    }

    [TestMethod]
    public void ProjectInsideASolution_UsesTheSolutionFolderAsContext()
    {
        using var t = new TempTree();
        t.Add("App.sln", "");
        t.Add("src/Api/Api.csproj", WebCsproj);

        var c = Detect(t).Single();

        Assert.AreEqual(t.Root, c.ContextDirectory);
        Assert.AreEqual("src/Api/Api.csproj", c.Facts["projectFile"]);
    }

    [TestMethod]
    public void ProjectWithReferenceOutsideItsFolder_UsesTheRootAsContext()
    {
        using var t = new TempTree();
        t.Add("Api/Api.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>" +
            "<ItemGroup><ProjectReference Include=\"..\\Shared\\Shared.csproj\" /></ItemGroup></Project>");
        t.Add("Shared/Shared.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");

        var c = Detect(t).Single();

        Assert.AreEqual(t.Root, c.ContextDirectory);
        Assert.AreEqual("Api/Api.csproj", c.Facts["projectFile"]);
    }

    [TestMethod]
    public void ExistingDockerfileInTheProjectFolder_BecomesTheContext()
    {
        using var t = new TempTree();
        t.Add("App.sln", "");
        t.Add("src/Api/Api.csproj", WebCsproj);
        t.Add("src/Api/Dockerfile", "FROM scratch");

        Assert.AreEqual(t.Full("src/Api"), Detect(t).Single().ContextDirectory);
    }

    [TestMethod]
    public void ExistingDockerfileInAParentFolder_BecomesTheContext_CaseInsensitively()
    {
        using var t = new TempTree();
        t.Add("src/Api/Api.csproj", WebCsproj);
        t.Add("src/dockerfile", "FROM scratch");

        Assert.AreEqual(t.Full("src"), Detect(t).Single().ContextDirectory);
    }

    [TestMethod]
    public void RenderedDockerfile_ForAWebProject_MatchesTheGolden()
    {
        using var t = new TempTree();
        t.Add("Api/Api.csproj", WebCsproj);
        var provider = new DotNetStackProvider();

        var text = provider.RenderDockerfile(provider.Detect(t.Root).Components.Single());

        var expected = string.Join("\n",
            "# Generated by ShipRight. Safe to edit - ShipRight never overwrites an existing Dockerfile.",
            "FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build",
            "WORKDIR /src",
            "COPY . .",
            "RUN dotnet restore \"Api.csproj\"",
            "RUN dotnet publish \"Api.csproj\" -c Release -o /app/publish --no-restore",
            "",
            "FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final",
            "WORKDIR /app",
            "ENV ASPNETCORE_HTTP_PORTS=8080",
            "EXPOSE 8080",
            "COPY --from=build /app/publish .",
            "USER $APP_UID",
            "ENTRYPOINT [\"dotnet\", \"Api.dll\"]",
            "");
        Assert.AreEqual(expected, text);
    }

    [TestMethod]
    public void RenderedDockerfile_ForAWorker_UsesTheRuntimeImageAndExposesNothing()
    {
        using var t = new TempTree();
        t.Add("Jobs/Jobs.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk.Worker\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        var provider = new DotNetStackProvider();

        var text = provider.RenderDockerfile(provider.Detect(t.Root).Components.Single());

        StringAssert.Contains(text, "FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final");
        Assert.IsFalse(text.Contains("EXPOSE"));
        Assert.IsFalse(text.Contains("ASPNETCORE"));
    }

    [TestMethod]
    public void RenderedDockerfile_BeforeDotNet8_DoesNotUseTheNonRootUserVariable()
    {
        using var t = new TempTree();
        t.Add("Api/Api.csproj", WebCsproj.Replace("net10.0", "net6.0"));
        var provider = new DotNetStackProvider();

        var text = provider.RenderDockerfile(provider.Detect(t.Root).Components.Single());

        Assert.IsFalse(text.Contains("APP_UID"));
        StringAssert.Contains(text, "aspnet:6.0");
        StringAssert.Contains(text, "ASPNETCORE_URLS=http://+:8080");
    }

    [TestMethod]
    public void RenderedDockerfile_UsesTheProjectPathRelativeToTheContext()
    {
        using var t = new TempTree();
        t.Add("App.sln", "");
        t.Add("src/Api/Api.csproj", WebCsproj);
        var provider = new DotNetStackProvider();

        var text = provider.RenderDockerfile(provider.Detect(t.Root).Components.Single());

        StringAssert.Contains(text, "RUN dotnet restore \"src/Api/Api.csproj\"");
    }

    [TestMethod]
    public void RenderedDockerIgnore_KeepsBuildOutputAndSecretsOutOfTheImage()
    {
        using var t = new TempTree();
        t.Add("Api/Api.csproj", WebCsproj);
        var provider = new DotNetStackProvider();

        var text = provider.RenderDockerIgnore(provider.Detect(t.Root).Components.Single());

        foreach (var entry in new[] { "**/bin/", "**/obj/", ".git", ".vs/", "**/.env" })
            StringAssert.Contains(text, entry);
    }
}
