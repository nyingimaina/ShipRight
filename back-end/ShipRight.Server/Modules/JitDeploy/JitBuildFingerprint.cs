using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ShipRight.Modules.JitDeploy;

/// <summary>Where one service's image comes from, found in the rendered compose file. <c>Unsupported</c> says why it cannot be tracked.</summary>
public sealed record BuildInputs(string Service, string ContextRel, string? DockerfileRel, string BuildJson);

public sealed record BuildInputsOutcome(BuildInputs? Inputs, string? Unsupported);

/// <summary>What git says about one service's inputs at one commit.</summary>
public sealed record ProbedInputs(string? ContextTree, string? DockerfileBlob, int UntrackedInContext);

public sealed record FingerprintProbe(string? ComposeBlob, IReadOnlyDictionary<string, ProbedInputs> Services);

/// <summary>
/// Reads the build section of each service from <c>docker compose config --format json</c> and turns it into repository-relative
/// inputs. Anything that cannot be proven to live inside the repository (a folder elsewhere, a git address, an odd path) is
/// reported as unsupported and never guessed at.
/// </summary>
public static class BuildInputsReader
{
    private static readonly Regex SafePath = new(@"^[A-Za-z0-9._@+/-]+$", RegexOptions.Compiled);

    public static IReadOnlyDictionary<string, BuildInputsOutcome> Read(string composeConfigJson, string repoDir)
    {
        var result = new Dictionary<string, BuildInputsOutcome>(StringComparer.Ordinal);
        var start = (composeConfigJson ?? "").IndexOf('{');
        if (start < 0) return result;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(composeConfigJson![start..]); }
        catch (JsonException) { return result; }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("services", out var services) || services.ValueKind != JsonValueKind.Object) return result;
            var repo = Normalize(repoDir);
            foreach (var s in services.EnumerateObject())
            {
                if (!s.Value.TryGetProperty("build", out var build) || build.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) continue;
                result[s.Name] = ReadOne(s.Name, build, repo);
            }
        }
        return result;
    }

    private static BuildInputsOutcome ReadOne(string service, JsonElement build, string repo)
    {
        string context; string? dockerfile = null; var inline = false;
        if (build.ValueKind == JsonValueKind.String) context = build.GetString() ?? ".";
        else if (build.ValueKind == JsonValueKind.Object)
        {
            context = build.TryGetProperty("context", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()! : ".";
            if (build.TryGetProperty("dockerfile", out var d) && d.ValueKind == JsonValueKind.String) dockerfile = d.GetString();
            inline = build.TryGetProperty("dockerfile_inline", out var di) && di.ValueKind == JsonValueKind.String;
        }
        else return new(null, "its build section is not in a form ShipRight can read.");

        if (context.Contains("://") || context.StartsWith("git@", StringComparison.Ordinal))
            return new(null, "it builds from a git address, which ShipRight cannot track.");
        var ctxAbs = Normalize(context.StartsWith('/') ? context : repo + "/" + context);
        var ctxRel = Relative(ctxAbs, repo);
        if (ctxRel is null) return new(null, "its build folder is outside the repository, so changes to it cannot be tracked.");
        if (!IsPlain(ctxRel)) return new(null, "its build folder name has unusual characters.");

        string? dfRel = null;
        if (!inline)
        {
            var df = dockerfile ?? "Dockerfile";
            var dfAbs = Normalize(df.StartsWith('/') ? df : ctxAbs + "/" + df);
            dfRel = Relative(dfAbs, repo);
            if (dfRel is null || dfRel == ".") return new(null, "its Dockerfile is outside the repository, so changes to it cannot be tracked.");
            if (!IsPlain(dfRel)) return new(null, "its Dockerfile name has unusual characters.");
        }

        return new(new BuildInputs(service, ctxRel, dfRel, CanonicalWithout(build, "context", "dockerfile")), null);
    }

    private static bool IsPlain(string rel) =>
        rel == "." || (SafePath.IsMatch(rel) && !rel.StartsWith('/') && !rel.Split('/').Any(p => p.Length == 0 || p == ".."));

    private static string? Relative(string abs, string repo)
    {
        if (abs == repo) return ".";
        return abs.StartsWith(repo + "/", StringComparison.Ordinal) ? abs[(repo.Length + 1)..] : null;
    }

    private static string Normalize(string path)
    {
        var parts = new List<string>();
        foreach (var seg in path.Replace('\\', '/').Split('/'))
        {
            if (seg.Length == 0 || seg == ".") continue;
            if (seg == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); continue; }
            parts.Add(seg);
        }
        return "/" + string.Join("/", parts);
    }

    private static string CanonicalWithout(JsonElement build, params string[] skip)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            if (build.ValueKind != JsonValueKind.Object) { w.WriteStartObject(); w.WriteEndObject(); }
            else Write(build, w, skip);
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void Write(JsonElement e, Utf8JsonWriter w, string[]? skipTop = null)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                w.WriteStartObject();
                foreach (var p in e.EnumerateObject().Where(p => skipTop is null || !skipTop.Contains(p.Name)).OrderBy(p => p.Name, StringComparer.Ordinal))
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

/// <summary>
/// A short fingerprint of everything an image's build reads that ShipRight can prove: the build folder's content (git's own
/// tree hash, so a change elsewhere in the repository cannot affect it), the Dockerfile, the compose file and the build
/// settings. The probe reads git objects only, so it needs no checkout and changes nothing on the server.
/// </summary>
public static class BuildFingerprint
{
    public const string Version = "1";

    public static string? Compute(BuildInputs inputs, ProbedInputs probed, string? composeBlob)
    {
        if (probed.ContextTree is null) return null;
        var text = string.Join("\n",
            "shipright-build-fingerprint-v" + Version,
            inputs.ContextRel,
            inputs.DockerfileRel ?? "(inline)",
            inputs.BuildJson,
            probed.ContextTree,
            probed.DockerfileBlob ?? "none",
            composeBlob ?? "none");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant()[..16];
    }

    public static string ProbeCommand(JitTarget t, string sha, IReadOnlyList<BuildInputs> inputs, string? composeFileRel, bool checkUntracked)
    {
        JitNames.Path(t.ProjectDir);
        var s = JitNames.Sha(sha);
        var lines = new List<string>();
        if (composeFileRel is not null)
            lines.Add($"echo \"compose|$(git rev-parse --verify --quiet {ShellQuote.Single(s + ":" + JitNames.RepoFile(composeFileRel))} 2>/dev/null || echo none)\"");
        foreach (var i in inputs)
        {
            var ctx = Plain(i.ContextRel);
            var ctxSpec = ctx == "." ? s + "^{tree}" : s + ":" + ctx;
            var dfPart = i.DockerfileRel is null
                ? "none"
                : $"$(git rev-parse --verify --quiet {ShellQuote.Single(s + ":" + Plain(i.DockerfileRel))} 2>/dev/null || echo none)";
            var stray = checkUntracked
                ? $"$(git status --porcelain --ignored -- {ShellQuote.Single(ctx)} 2>/dev/null | wc -l | tr -d ' ')"
                : "0";
            lines.Add($"echo \"svc|{JitNames.Service(i.Service)}|$(git rev-parse --verify --quiet {ShellQuote.Single(ctxSpec)} 2>/dev/null || echo none)|{dfPart}|{stray}\"");
        }
        return $"cd {ShellQuote.Single(t.RepoDir)} && {{ {string.Join("; ", lines)}; }}";
    }

    private static string Plain(string rel)
    {
        if (rel != "." && (!Regex.IsMatch(rel, @"^[A-Za-z0-9._@+/-]+$") || rel.StartsWith('/') || rel.Split('/').Any(p => p.Length == 0 || p == "..")))
            throw new ArgumentException($"'{rel}' is not a plain path inside the repository.");
        return rel;
    }

    public static FingerprintProbe ParseProbe(IEnumerable<string> lines)
    {
        string? compose = null;
        var services = new Dictionary<string, ProbedInputs>(StringComparer.Ordinal);
        foreach (var raw in lines)
        {
            var parts = (raw ?? "").Trim().Split('|');
            if (parts[0] == "compose" && parts.Length >= 2) compose = parts[1] == "none" || parts[1].Length == 0 ? null : parts[1];
            else if (parts[0] == "svc" && parts.Length >= 5)
                services[parts[1]] = new ProbedInputs(
                    parts[2] is "none" or "" ? null : parts[2],
                    parts[3] is "none" or "" ? null : parts[3],
                    int.TryParse(parts[4], out var n) ? n : 0);
        }
        return new FingerprintProbe(compose, services);
    }

    public static string ImageIdCommand(string imageRef) =>
        $"docker image inspect -f '{{{{.Id}}}}' {ShellQuote.Single(JitNames.ImageRef(imageRef))} 2>/dev/null";
}

/// <summary>
/// Shadow mode: works out, after the build, which images could have been reused, and checks that against what really
/// happened (did the rebuilt image come out identical?). It only reports; nothing is skipped and nothing is changed.
/// </summary>
public static class JitFingerprintShadow
{
    public static async Task ReportAsync(
        Func<string, Task<(int Exit, IReadOnlyList<string> Lines)>> exec,
        Func<string, Task> say,
        JitTarget target,
        string composeConfigJson,
        string? composeFileRel,
        IReadOnlyList<(string Service, string ImageRef)> services,
        string newSha, string newTag,
        string? prevSha, string? prevTag,
        TimeSpan buildTime,
        bool rebuildAll = false)
    {
        var outcomes = BuildInputsReader.Read(composeConfigJson, target.RepoDir);
        var usable = new List<(BuildInputs Inputs, string ImageRef)>();
        foreach (var (svc, imageRef) in services)
        {
            if (!outcomes.TryGetValue(svc, out var o)) continue;
            if (o.Inputs is null) await say($"[fingerprint] {svc}: not computed ({o.Unsupported})");
            else usable.Add((o.Inputs, imageRef));
        }

        var buildSeconds = buildTime.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture);
        if (usable.Count == 0 || prevSha is null || prevTag is null)
        {
            if (usable.Count > 0)
                await say("[fingerprint] This is the first release on this server, so there is nothing to compare with yet; fingerprints are compared from the next deploy on.");
            return;
        }

        var clock = global::System.Diagnostics.Stopwatch.StartNew();
        var inputs = usable.Select(u => u.Inputs).ToList();
        var now = await exec(BuildFingerprint.ProbeCommand(target, newSha, inputs, composeFileRel, checkUntracked: true));
        var before = await exec(BuildFingerprint.ProbeCommand(target, prevSha, inputs, composeFileRel, checkUntracked: false));
        if (now.Exit != 0 || before.Exit != 0)
        {
            await say("[fingerprint] could not read the build folders from git, so no comparison was made.");
            return;
        }
        var probeNow = BuildFingerprint.ParseProbe(now.Lines);
        var probeBefore = BuildFingerprint.ParseProbe(before.Lines);

        var lines = new List<string>();
        foreach (var (i, imageRef) in usable)
        {
            probeNow.Services.TryGetValue(i.Service, out var pn);
            probeBefore.Services.TryGetValue(i.Service, out var pb);
            var fpNow = pn is null ? null : BuildFingerprint.Compute(i, pn, probeNow.ComposeBlob);
            var fpBefore = pb is null ? null : BuildFingerprint.Compute(i, pb, probeBefore.ComposeBlob);
            var idNow = await ImageId(exec, $"{imageRef}:{newTag}");
            var idBefore = await ImageId(exec, $"{imageRef}:{prevTag}");
            lines.Add(Describe(i.Service, prevTag, fpBefore, fpNow, pn?.UntrackedInContext ?? 0, idNow, idBefore));
        }
        clock.Stop();

        foreach (var l in lines) await say(l);
        if (rebuildAll) await say("[fingerprint] 'Rebuild everything' was chosen, so no image would be reused on this deploy whatever the fingerprints say.");
        await say($"[fingerprint] Shadow mode: nothing is skipped yet and every image is still built. Fingerprints took {clock.ElapsedMilliseconds} ms; the build took {buildSeconds} s.");
    }

    private static async Task<string?> ImageId(Func<string, Task<(int Exit, IReadOnlyList<string> Lines)>> exec, string imageRef)
    {
        try
        {
            var r = await exec(BuildFingerprint.ImageIdCommand(imageRef));
            return r.Exit == 0 ? r.Lines.Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) : null;
        }
        catch (ArgumentException) { return null; }
    }

    public static string Describe(string service, string prevTag, string? fpBefore, string? fpNow, int untracked, string? idNow, string? idBefore)
    {
        if (fpNow is null || fpBefore is null)
            return $"[fingerprint] {service}: could not be computed (its build folder was not found in git for one of the two commits).";
        if (untracked > 0)
            return $"[fingerprint] {service}: not reliable here: {untracked} item(s) in its build folder are not tracked by git (they may still be sent to the build), so this image would always be rebuilt.";
        bool? sameImage = idNow is not null && idBefore is not null ? idNow == idBefore : null;
        if (fpNow == fpBefore)
            return sameImage switch
            {
                true => $"[fingerprint] {service}: unchanged since {prevTag} ({fpNow}); the build could have been skipped; prediction confirmed: the rebuilt image is identical.",
                false => $"[fingerprint] {service}: WRONG PREDICTION: the fingerprint ({fpNow}) says unchanged since {prevTag}, but the rebuilt image is different. Please report this; skipping stays off.",
                _ => $"[fingerprint] {service}: unchanged since {prevTag} ({fpNow}); the build could have been skipped (the earlier image is not on this server to double-check).",
            };
        return sameImage == true
            ? $"[fingerprint] {service}: changed since {prevTag} ({fpBefore} → {fpNow}), yet the rebuilt image is identical, so that build was not needed (the fingerprint is cautious)."
            : $"[fingerprint] {service}: changed since {prevTag} ({fpBefore} → {fpNow}); a build was needed.";
    }
}
