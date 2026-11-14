using System.Text.RegularExpressions;
using ShipRight.Modules.Projects;

namespace ShipRight.Modules.BuildRecipes;

/// <summary>
/// How one service's image is made: which Dockerfile (a path relative to the build context, or absolute), which stage of
/// it, and which build arguments. Empty means exactly what has always happened (<c>docker build -t image:tag context</c>),
/// so nothing changes for a service that never set one.
/// </summary>
public sealed record BuildRecipe(string? DockerfilePath, string? Target, IReadOnlyDictionary<string, string>? Args)
{
    private static readonly Regex TargetShape = new(@"^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.Compiled);
    private static readonly Regex ArgName = new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    public static BuildRecipe Of(ServiceConfig s) => new(
        Clean(s.DockerfilePath), Clean(s.BuildTarget), s.BuildArgs is { Count: > 0 } a ? a : null);

    public bool IsEmpty => DockerfilePath is null && Target is null && Args is null;

    private static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    /// <summary>Plain-language reasons this recipe cannot be used. Nothing here is ever run by a shell, but a wrong value would build the wrong image.</summary>
    public IReadOnlyList<string> Problems()
    {
        var p = new List<string>();
        if (DockerfilePath is { } f)
        {
            if (f.Any(char.IsControl)) p.Add("The Dockerfile path contains a character that is not allowed.");
            else if (f.StartsWith('-')) p.Add($"The Dockerfile path '{f}' must be a path, not an option.");
        }
        if (Target is { } t && !TargetShape.IsMatch(t))
            p.Add($"The build target '{Printable(t)}' is not valid. Use the name of a stage in the Dockerfile (letters, digits, . _ -).");
        foreach (var (k, v) in Args ?? new Dictionary<string, string>())
        {
            if (!ArgName.IsMatch(k)) p.Add($"The build argument name '{Printable(k)}' is not valid. Use letters, digits and _ only, not starting with a digit.");
            else if (v is not null && v.Any(char.IsControl)) p.Add($"The value of build argument '{k}' contains a line break or another character that is not allowed.");
        }
        return p;
    }

    public bool SameAs(BuildRecipe other) =>
        DockerfilePath == other.DockerfilePath && Target == other.Target &&
        (Args ?? new Dictionary<string, string>()).OrderBy(x => x.Key, StringComparer.Ordinal)
            .SequenceEqual((other.Args ?? new Dictionary<string, string>()).OrderBy(x => x.Key, StringComparer.Ordinal));

    private static string Printable(string v) => new(v.Select(c => char.IsControl(c) ? '?' : c).ToArray());
}

/// <summary>Adds a recipe to a <c>docker build</c> command, just before its tag. Everything else is left exactly as it was.</summary>
public static class DockerBuildRecipe
{
    public static string[] Apply(IReadOnlyList<string> args, BuildRecipe recipe)
    {
        if (recipe.IsEmpty || args.Count == 0 || args[0] != "build") return args.ToArray();
        var tag = args.ToList().IndexOf("-t");
        if (tag < 0 || tag + 1 >= args.Count) return args.ToArray();

        var problems = recipe.Problems();
        if (problems.Count > 0)
            throw new InvalidOperationException("The build settings for this service are not valid, so nothing was built. " + string.Join(" ", problems));

        var context = args[^1];
        var add = new List<string>();
        if (recipe.DockerfilePath is { } file) { add.Add("-f"); add.Add(Join(context, file)); }
        if (recipe.Target is { } target) { add.Add("--target"); add.Add(target); }
        foreach (var (k, v) in (recipe.Args ?? new Dictionary<string, string>()).OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            add.Add("--build-arg");
            add.Add($"{k}={v}");
        }
        return [.. args.Take(tag), .. add, .. args.Skip(tag)];
    }

    /// <summary>The Dockerfile's location: as given when absolute, otherwise inside the context, written in the context's own style.</summary>
    internal static string Join(string context, string file)
    {
        if (file.StartsWith('/') || file.StartsWith("\\\\") || Regex.IsMatch(file, @"^[A-Za-z]:[\\/]")) return file;
        var sep = context.Contains('\\') && !context.Contains('/') ? '\\' : '/';
        return context.TrimEnd('/', '\\') + sep + file.Replace('/', sep).Replace('\\', sep);
    }
}
