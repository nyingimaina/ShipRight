using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace ShipRight.Modules.JitDeploy;

public sealed record StackComparison(bool Same, IReadOnlyList<string> Differences);

/// <summary>
/// The "stack" folder beside the checkout. It holds a tiny compose.yaml that includes the real compose file where it is (so
/// relative paths in it keep working), a .env with the variables and the running release's image tags, and a <c>dc</c> helper.
/// A person can then run plain <c>docker compose ps</c>, <c>logs</c> or <c>down</c> there with no options at all. It is a
/// convenience copy: deploys keep passing everything explicitly. ShipRight only ever rewrites files that carry its marker.
/// </summary>
public static class JitStack
{
    public const string Marker = "Managed by ShipRight";

    public static string Dir(JitTarget t) { JitNames.Path(t.ProjectDir); return t.ProjectDir + "/stack"; }

    private static string Yaml(string v) => "\"" + v.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    public static string ComposeYaml(JitTarget t, string project, string composeFileRel, string? overrideFile)
    {
        var repo = t.RepoDir; JitNames.Path(t.ProjectDir);
        var name = JitNames.Project(project);
        var file = repo + "/" + JitNames.RepoFile(composeFileRel);
        var sb = new StringBuilder();
        sb.Append($"# {Marker}. It is rewritten after every successful deploy, so do not edit it.\n");
        sb.Append("# Run docker compose from this folder with no extra options, for example: docker compose ps\n");
        sb.Append($"name: {name}\n");
        sb.Append("include:\n");
        sb.Append("  - path:\n");
        sb.Append($"      - {Yaml(file)}\n");
        if (overrideFile is not null) sb.Append($"      - {Yaml(JitNames.Path(overrideFile))}\n");
        sb.Append($"    project_directory: {Yaml(repo)}\n");
        sb.Append($"    env_file: {Yaml(Dir(t) + "/.env")}\n");
        return sb.ToString();
    }

    public static string EnvText(string? variables, IReadOnlyList<TagBinding> bindings, string tag)
    {
        if (!ReleaseTag.TryParse(tag, out _, out _)) throw new ArgumentException($"'{tag}' is not a release tag.", nameof(tag));
        var sb = new StringBuilder();
        sb.Append($"# {Marker}. Rewritten after every successful deploy; change values in ShipRight, not here.\n");
        if (!string.IsNullOrWhiteSpace(variables))
        {
            sb.Append(variables.TrimEnd('\r', '\n').Replace("\r\n", "\n")).Append('\n');
        }
        sb.Append("# The release that is running\n");
        foreach (var b in bindings)
            sb.Append($"{JitNames.TagVariable(b.TagVariable)}={JitNames.ImageRef(b.ImageRef)}:{tag}\n");
        return sb.ToString();
    }

    public static string Script(ComposeContext ctx, IReadOnlyList<TagBinding> bindings, string tag) =>
        "#!/bin/sh\n" +
        $"# {Marker}. Runs docker compose for this project exactly as ShipRight does, so no options are needed.\n" +
        "# Examples:  ./dc ps    ./dc logs -f <service>    ./dc down    ./dc up -d --no-build --pull never\n" +
        JitCommands.ComposeByHand(ctx, bindings, tag) + "\n";

    private static string Marked(string name) => $": 'stack:{name}'; ";

    /// <summary>Writes the three files. A file that exists without the marker is left alone, and the command says so.</summary>
    public static string WriteCommand(JitTarget t, string composeYaml, string env, string script)
    {
        var dir = ShellQuote.Single(Dir(t));
        string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));
        return Marked("write") +
               $"d={dir}; umask 077; mkdir -p \"$d\" || exit 1; " +
               "w() { f=\"$d/$1\"; " +
               $"if [ -e \"$f\" ] && ! grep -q '{Marker}' \"$f\"; then echo \"[stack] $1 already exists and is not managed by ShipRight, so it was left alone.\"; return 1; fi; " +
               "echo \"$3\" | base64 -d > \"$f.tmp\" && chmod \"$2\" \"$f.tmp\" && mv -f \"$f.tmp\" \"$f\"; }; " +
               $"w compose.yaml 644 '{B64(composeYaml)}' && w .env 600 '{B64(env)}' && w dc 755 '{B64(script)}'";
    }

    /// <summary>Removes the compose.yaml and .env this feature wrote (never a file without the marker); the dc helper stays.</summary>
    public static string RemoveShortcutCommand(JitTarget t)
    {
        var dir = ShellQuote.Single(Dir(t));
        return Marked("remove") +
               $"d={dir}; for n in compose.yaml .env; do p=\"$d/$n\"; if [ -f \"$p\" ] && grep -q '{Marker}' \"$p\"; then rm -f \"$p\"; fi; done; true";
    }

    public static string ConfigViaStackCommand(JitTarget t) =>
        Marked("config") + $"cd {ShellQuote.Single(Dir(t))} && docker compose config --format json";

    public static string VersionCommand => Marked("version") + "docker compose version --short";

    /// <summary>Prints the name of the compose file in the repository, in the order compose itself looks for one. Prints nothing when there is none.</summary>
    public static string DetectComposeFileCommand(JitTarget t)
    {
        JitNames.Path(t.ProjectDir);
        return Marked("detect") +
               $"cd {ShellQuote.Single(t.RepoDir)} 2>/dev/null && for f in compose.yaml compose.yml docker-compose.yaml docker-compose.yml; do if [ -f \"$f\" ]; then echo \"$f\"; break; fi; done; true";
    }

    /// <summary>Compares two renderings of the compose file. Only service names are ever reported, never values.</summary>
    public static StackComparison Compare(string explicitJson, string stackJson)
    {
        if (!TryParse(explicitJson, out var a) || !TryParse(stackJson, out var b))
            return new(false, ["could not be read"]);
        using (a) using (b)
        {
            var diffs = new List<string>();
            var sa = Services(a!.RootElement); var sb = Services(b!.RootElement);
            foreach (var name in sa.Keys.Union(sb.Keys).OrderBy(n => n, StringComparer.Ordinal))
            {
                if (!sa.TryGetValue(name, out var x) || !sb.TryGetValue(name, out var y) || Canon(x) != Canon(y)) diffs.Add(name);
            }
            if (Canon(a.RootElement, "services") != Canon(b.RootElement, "services")) diffs.Add("other settings");
            return new(diffs.Count == 0, diffs);
        }
    }

    private static bool TryParse(string json, out JsonDocument? doc)
    {
        doc = null;
        var start = (json ?? "").IndexOf('{');
        if (start < 0) return false;
        try { doc = JsonDocument.Parse(json![start..]); return doc.RootElement.ValueKind == JsonValueKind.Object; }
        catch (JsonException) { return false; }
    }

    private static Dictionary<string, JsonElement> Services(JsonElement root) =>
        root.TryGetProperty("services", out var s) && s.ValueKind == JsonValueKind.Object
            ? s.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal)
            : new Dictionary<string, JsonElement>();

    private static string Canon(JsonElement e, string? skipTop = null)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            Write(e, w, skipTop);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void Write(JsonElement e, Utf8JsonWriter w, string? skipTop = null)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                w.WriteStartObject();
                foreach (var p in e.EnumerateObject().Where(p => p.Name != skipTop).OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    w.WritePropertyName(p.Name);
                    Write(p.Value, w);
                }
                w.WriteEndObject();
                break;
            case JsonValueKind.Array:
                w.WriteStartArray();
                foreach (var i in e.EnumerateArray()) Write(i, w);
                w.WriteEndArray();
                break;
            default:
                e.WriteTo(w);
                break;
        }
    }
}
