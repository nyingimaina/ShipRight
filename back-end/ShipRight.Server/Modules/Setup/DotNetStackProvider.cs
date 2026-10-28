using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ShipRight.Modules.Setup;

public sealed class DotNetStackProvider : IStackProvider
{
    private const string DefaultDotNetVersion = "10.0";
    private const int WebPort = 8080;

    public string Id => "dotnet";
    public string DisplayName => ".NET";
    public IReadOnlyList<string> GitIgnoreEntries { get; } = ["bin/", "obj/", ".vs/", "*.user"];

    public StackDetection Detect(string rootPath)
    {
        var components = new List<DetectedComponent>();
        var warnings = new List<string>();

        foreach (var file in StackFiles.Find(rootPath, n => n.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase), maxDepth: 4))
        {
            var fileName = Path.GetFileName(file);
            XDocument doc;
            try { doc = XDocument.Load(file); }
            catch (Exception ex) when (ex is global::System.Xml.XmlException or IOException or UnauthorizedAccessException)
            {
                warnings.Add($"{fileName} could not be read ({ex.Message.Split('\n')[0].Trim()}), so it was skipped.");
                continue;
            }

            var component = Inspect(rootPath, file, doc, warnings);
            if (component is not null) components.Add(component);
        }

        return new StackDetection(components, warnings);
    }

    private static DetectedComponent? Inspect(string rootPath, string file, XDocument doc, List<string> warnings)
    {
        var project = doc.Root;
        if (project is null || project.Name.LocalName != "Project") return null;

        var fileName = Path.GetFileName(file);
        var sdk = project.Attribute("Sdk")?.Value;
        if (sdk is null)
        {
            warnings.Add($"{fileName} is a .NET Framework project, which ShipRight cannot put in a container yet, so it was skipped.");
            return null;
        }

        // Only direct PropertyGroup children count, so a <Version> nested in a PackageReference is never mistaken for the project's.
        string? Prop(string name) =>
            project.Elements().Where(g => g.Name.LocalName == "PropertyGroup")
                .Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim();

        var outputType = Prop("OutputType");
        var frameworks = (Prop("TargetFrameworks") ?? Prop("TargetFramework") ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (IsTestProject(project, fileName)) return null;
        if (string.Equals(outputType, "WinExe", StringComparison.OrdinalIgnoreCase)) return null;
        if (frameworks.Any(f => f.Contains("-windows", StringComparison.OrdinalIgnoreCase))) return null;
        if (IsTrue(Prop("UseWPF")) || IsTrue(Prop("UseWindowsForms"))) return null;

        string kind;
        Confidence confidence;
        if (sdk.StartsWith("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase))
        {
            kind = "aspnet-web"; confidence = Confidence.High;
        }
        else if (sdk.StartsWith("Microsoft.NET.Sdk.Worker", StringComparison.OrdinalIgnoreCase))
        {
            kind = "dotnet-worker"; confidence = Confidence.High;
        }
        else if (string.Equals(outputType, "Exe", StringComparison.OrdinalIgnoreCase))
        {
            kind = "dotnet-worker"; confidence = Confidence.Medium;
        }
        else return null;

        var projectDir = Path.GetDirectoryName(file)!;
        var context = ResolveContext(rootPath, projectDir, project);
        var name = Path.GetFileNameWithoutExtension(file);

        var facts = new Dictionary<string, string>
        {
            ["projectFile"] = StackFiles.RelativeTo(context, file),
            ["assemblyName"] = Prop("AssemblyName") is { Length: > 0 } asm ? asm : name,
            ["dotnetVersion"] = HighestFramework(frameworks),
        };

        return new DetectedComponent(
            "dotnet", kind, name, projectDir, context, confidence,
            kind == "aspnet-web" ? WebPort : null,
            StackFiles.NormalizeVersion(Prop("Version") ?? Prop("VersionPrefix")),
            facts, []);
    }

    private static bool IsTrue(string? v) => string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);

    private static bool IsTestProject(XElement project, string fileName)
    {
        var nameBase = Path.GetFileNameWithoutExtension(fileName);
        if (nameBase.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase) ||
            nameBase.EndsWith(".Test", StringComparison.OrdinalIgnoreCase)) return true;

        if (project.Descendants().Any(e => e.Name.LocalName == "IsTestProject" && IsTrue(e.Value.Trim()))) return true;

        return project.Descendants().Any(e =>
            e.Name.LocalName == "PackageReference" &&
            string.Equals(e.Attribute("Include")?.Value, "Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Where docker builds from. An existing Dockerfile wins (nearest one from the project folder
    /// up to the root); otherwise the solution folder, or the root when the project reaches outside its own folder.
    /// </summary>
    private static string ResolveContext(string rootPath, string projectDir, XElement project)
    {
        var existing = AncestorsWithin(rootPath, projectDir)
            .FirstOrDefault(d => StackFiles.DockerfileIn(d) is not null);
        if (existing is not null) return existing;

        var solutionDir = AncestorsWithin(rootPath, projectDir).FirstOrDefault(HasSolutionFile);
        if (solutionDir is not null) return solutionDir;

        var referencesOutside = project.Descendants()
            .Where(e => e.Name.LocalName == "ProjectReference")
            .Select(e => e.Attribute("Include")?.Value ?? "")
            .Any(v => v.Replace('\\', '/').Contains("../"));
        return referencesOutside ? rootPath : projectDir;
    }

    private static IEnumerable<string> AncestorsWithin(string rootPath, string startDir)
    {
        var current = startDir;
        while (StackFiles.IsSameOrBelow(rootPath, current))
        {
            yield return current;
            if (string.Equals(current, rootPath, StringComparison.OrdinalIgnoreCase)) yield break;
            var parent = Path.GetDirectoryName(current);
            if (parent is null || parent == current) yield break;
            current = parent;
        }
    }

    private static bool HasSolutionFile(string dir)
    {
        try
        {
            return Directory.GetFiles(dir).Any(f =>
                f.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
                f.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase));
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static string HighestFramework(IEnumerable<string> frameworks)
    {
        Version? best = null;
        foreach (var f in frameworks)
        {
            var m = Regex.Match(f, @"^net(?:coreapp)?(\d+\.\d+)");
            if (m.Success && Version.TryParse(m.Groups[1].Value, out var v) && (best is null || v > best)) best = v;
        }
        return best is null ? DefaultDotNetVersion : best.ToString(2);
    }

    public string RenderDockerfile(DetectedComponent c)
    {
        var version = c.Facts["dotnetVersion"];
        var major = int.Parse(version.Split('.')[0], CultureInfo.InvariantCulture);
        var project = c.Facts["projectFile"];
        var web = c.Kind == "aspnet-web";

        var lines = new List<string>
        {
            StackFiles.GeneratedHeader,
            $"FROM mcr.microsoft.com/dotnet/sdk:{version} AS build",
            "WORKDIR /src",
            "COPY . .",
            $"RUN dotnet restore \"{project}\"",
            $"RUN dotnet publish \"{project}\" -c Release -o /app/publish --no-restore",
            "",
            $"FROM mcr.microsoft.com/dotnet/{(web ? "aspnet" : "runtime")}:{version} AS final",
            "WORKDIR /app",
        };
        if (web)
        {
            lines.Add(major >= 8 ? $"ENV ASPNETCORE_HTTP_PORTS={WebPort}" : $"ENV ASPNETCORE_URLS=http://+:{WebPort}");
            lines.Add($"EXPOSE {WebPort}");
        }
        lines.Add("COPY --from=build /app/publish .");
        if (major >= 8) lines.Add("USER $APP_UID");
        lines.Add($"ENTRYPOINT [\"dotnet\", \"{c.Facts["assemblyName"]}.dll\"]");
        return StackFiles.Lines(lines.ToArray());
    }

    public string RenderDockerIgnore(DetectedComponent c) => StackFiles.Lines(
        "# Keeps build output, git history and secrets out of the image build.",
        "**/bin/",
        "**/obj/",
        "**/.vs/",
        "**/*.user",
        ".git",
        "**/.env",
        "**/.env.*",
        "!**/.env.example",
        "**/node_modules");
}
