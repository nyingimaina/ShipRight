using System.Text.Json;
using System.Text.RegularExpressions;

namespace ShipRight.Modules.Setup;

public sealed class NodeStackProvider : IStackProvider
{
    private const string DefaultNodeVersion = "24";
    private const int ServerPort = 3000;
    private const int SpaPort = 80;

    private static readonly string[] ServerFrameworks =
        ["express", "fastify", "koa", "@nestjs/core", "@hapi/hapi", "hapi", "restify", "hono"];

    private static readonly JsonDocumentOptions Lenient = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    public string Id => "node";
    public string DisplayName => "Node.js";
    public IReadOnlyList<string> GitIgnoreEntries { get; } = ["node_modules/", ".next/", "dist/"];

    public StackDetection Detect(string rootPath)
    {
        var components = new List<DetectedComponent>();
        var warnings = new List<string>();

        foreach (var file in StackFiles.Find(rootPath, n => n == "package.json", maxDepth: 4))
        {
            var rel = StackFiles.RelativeTo(rootPath, file);
            JsonDocument doc;
            try { doc = JsonDocument.Parse(File.ReadAllText(file), Lenient); }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                warnings.Add($"{rel} could not be read as JSON, so it was skipped.");
                continue;
            }

            using (doc)
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;
                var component = Inspect(Path.GetDirectoryName(file)!, doc.RootElement);
                if (component is not null) components.Add(component);
            }
        }

        return new StackDetection(components, warnings);
    }

    private static DetectedComponent? Inspect(string dir, JsonElement pkg)
    {
        if (pkg.TryGetProperty("workspaces", out _)) return null; // a monorepo root; its packages are found on their own

        var deps = DependencyNames(pkg);
        var scripts = Scripts(pkg);
        var hasBuild = scripts.Contains("build");
        var hasStart = scripts.Contains("start");

        string kind;
        Confidence confidence;
        string? outputDir = null;
        var notes = new List<string>();

        if (deps.Contains("next"))
        {
            kind = "nextjs"; confidence = Confidence.High;
        }
        else if (ServerFrameworks.Any(deps.Contains))
        {
            kind = "node-server"; confidence = Confidence.High;
        }
        else if (hasBuild && SpaOutput(deps) is { } spa)
        {
            kind = "spa"; confidence = spa.Known ? Confidence.High : Confidence.Medium;
            outputDir = spa.Dir;
            if (!spa.Known)
                notes.Add("Angular's build output folder is not known, so check the COPY --from=build line in the Dockerfile.");
        }
        else if (hasStart)
        {
            kind = "node-server"; confidence = Confidence.Low;
        }
        else return null;

        var main = Str(pkg, "main");
        if (kind == "node-server" && !hasStart && main is null)
            notes.Add("There is no start script, so the Dockerfile runs 'node index.js'. Change the CMD line if that is wrong.");

        var (manager, lockFile, install) = PackageManager(dir);
        var facts = new Dictionary<string, string>
        {
            ["packageManager"] = manager,
            ["lockFile"] = lockFile,
            ["installCommand"] = install,
            ["nodeVersion"] = NodeVersion(dir, pkg),
            ["hasBuildScript"] = hasBuild ? "true" : "false",
            ["hasStartScript"] = hasStart ? "true" : "false",
            ["mainFile"] = main ?? "",
            ["outputDir"] = outputDir ?? "",
        };

        return new DetectedComponent(
            "node", kind, Name(dir, pkg), dir, dir, confidence,
            kind == "spa" ? SpaPort : ServerPort,
            StackFiles.NormalizeVersion(Str(pkg, "version")),
            facts, notes);
    }

    private static HashSet<string> DependencyNames(JsonElement pkg)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var section in new[] { "dependencies", "devDependencies" })
            if (pkg.TryGetProperty(section, out var obj) && obj.ValueKind == JsonValueKind.Object)
                foreach (var p in obj.EnumerateObject()) names.Add(p.Name);
        return names;
    }

    private static HashSet<string> Scripts(JsonElement pkg)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (pkg.TryGetProperty("scripts", out var obj) && obj.ValueKind == JsonValueKind.Object)
            foreach (var p in obj.EnumerateObject()) names.Add(p.Name);
        return names;
    }

    private static (string Dir, bool Known)? SpaOutput(HashSet<string> deps)
    {
        if (deps.Contains("react-scripts")) return ("build", true);
        if (deps.Contains("vite") || deps.Contains("@vue/cli-service") || deps.Contains("parcel")) return ("dist", true);
        if (deps.Contains("@angular/cli")) return ("dist", false);
        return null;
    }

    private static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Name(string dir, JsonElement pkg)
    {
        var name = Str(pkg, "name");
        if (string.IsNullOrWhiteSpace(name)) return Path.GetFileName(dir);
        var slash = name.LastIndexOf('/');
        return slash >= 0 ? name[(slash + 1)..] : name;
    }

    private static (string Manager, string LockFile, string Install) PackageManager(string dir)
    {
        if (File.Exists(Path.Combine(dir, "pnpm-lock.yaml"))) return ("pnpm", "pnpm-lock.yaml", "pnpm install --frozen-lockfile");
        if (File.Exists(Path.Combine(dir, "yarn.lock"))) return ("yarn", "yarn.lock", "yarn install --frozen-lockfile");
        if (File.Exists(Path.Combine(dir, "package-lock.json"))) return ("npm", "package-lock.json", "npm ci");
        return ("npm", "", "npm install");
    }

    private static string NodeVersion(string dir, JsonElement pkg)
    {
        string? hint = null;
        if (pkg.TryGetProperty("engines", out var engines) && engines.ValueKind == JsonValueKind.Object)
            hint = Str(engines, "node");

        if (hint is null)
            foreach (var f in new[] { ".nvmrc", ".node-version" })
            {
                var path = Path.Combine(dir, f);
                if (!File.Exists(path)) continue;
                hint = File.ReadAllText(path);
                break;
            }

        var m = hint is null ? Match.Empty : Regex.Match(hint, @"\d+");
        return m.Success ? m.Value : DefaultNodeVersion;
    }

    public string RenderDockerfile(DetectedComponent c)
    {
        var f = c.Facts;
        var manager = f["packageManager"];
        var node = f["nodeVersion"];
        var lockFile = f["lockFile"];
        var hasBuild = f["hasBuildScript"] == "true";
        var buildCommand = manager switch { "yarn" => "yarn build", "pnpm" => "pnpm run build", _ => "npm run build" };

        var lines = new List<string>
        {
            StackFiles.GeneratedHeader,
            $"FROM node:{node}-slim AS build",
            "WORKDIR /app",
        };
        if (manager != "npm") lines.Add("RUN corepack enable");
        lines.Add(lockFile.Length > 0 ? $"COPY package.json {lockFile} ./" : "COPY package.json ./");
        lines.Add($"RUN {f["installCommand"]}");
        lines.Add("COPY . .");
        if (hasBuild) lines.Add($"RUN {buildCommand}");

        if (c.Kind == "spa")
        {
            lines.AddRange(
            [
                "",
                "FROM nginx:alpine AS final",
                "RUN printf 'server {\\n  listen 80;\\n  root /usr/share/nginx/html;\\n  location / {\\n    try_files $uri $uri/ /index.html;\\n  }\\n}\\n' > /etc/nginx/conf.d/default.conf",
                $"COPY --from=build /app/{f["outputDir"]} /usr/share/nginx/html",
                $"EXPOSE {SpaPort}",
            ]);
            return StackFiles.Lines(lines.ToArray());
        }

        lines.Add(manager switch
        {
            "yarn" => "RUN yarn install --production --frozen-lockfile --ignore-scripts",
            "pnpm" => "RUN pnpm prune --prod",
            _ => "RUN npm prune --omit=dev",
        });
        lines.AddRange(
        [
            "",
            $"FROM node:{node}-slim AS final",
            "WORKDIR /app",
            "ENV NODE_ENV=production",
            "COPY --from=build --chown=node:node /app ./",
            "USER node",
            $"EXPOSE {ServerPort}",
            $"CMD {StartCommand(c)}",
        ]);
        return StackFiles.Lines(lines.ToArray());
    }

    private static string StartCommand(DetectedComponent c)
    {
        if (c.Facts["hasStartScript"] == "true") return "[\"npm\", \"start\"]";
        if (c.Kind == "nextjs") return "[\"npx\", \"next\", \"start\"]";
        var main = c.Facts["mainFile"];
        return $"[\"node\", \"{(main.Length > 0 ? main : "index.js")}\"]";
    }

    public string RenderDockerIgnore(DetectedComponent c) => StackFiles.Lines(
        "# Keeps dependencies, git history and secrets out of the image build.",
        "node_modules",
        ".git",
        ".env",
        ".env.*",
        "!.env.example",
        ".next",
        "dist",
        "coverage",
        "npm-debug.log*");
}
