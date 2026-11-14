using System.Text.RegularExpressions;

namespace ShipRight.Modules.JitDeploy;

/// <summary>
/// The image tag for one build-on-server release: <c>r&lt;counter&gt;-&lt;short sha&gt;</c>, e.g. <c>r42-ab8b4a9</c>.
/// Neutral by design: it is a plain docker tag with nothing that points back to ShipRight.
/// </summary>
public static class ReleaseTag
{
    private const int ShaLength = 7;
    private static readonly Regex Pattern = new($"^r([1-9][0-9]*)-([0-9a-f]{{{ShaLength}}})$", RegexOptions.Compiled);
    private static readonly Regex Hex = new("^[0-9a-f]+$", RegexOptions.Compiled);

    public static string Format(int counter, string sha)
    {
        if (counter < 1) throw new ArgumentException("A release counter starts at 1.", nameof(counter));
        var s = (sha ?? string.Empty).Trim().ToLowerInvariant();
        if (s.Length < ShaLength || !Hex.IsMatch(s))
            throw new ArgumentException($"'{sha}' is not a commit id (at least {ShaLength} hex characters).", nameof(sha));
        return $"r{counter}-{s[..ShaLength]}";
    }

    public static bool TryParse(string? tag, out int counter, out string shortSha)
    {
        counter = 0; shortSha = string.Empty;
        if (string.IsNullOrEmpty(tag)) return false;
        var m = Pattern.Match(tag);
        if (!m.Success || !int.TryParse(m.Groups[1].Value, out counter)) { counter = 0; return false; }
        shortSha = m.Groups[2].Value;
        return true;
    }

    /// <summary>One after the highest existing release counter; tags that are not release tags are ignored.</summary>
    public static int NextCounter(IEnumerable<string> existingTags)
    {
        var highest = 0;
        foreach (var t in existingTags)
            if (TryParse(t, out var c, out _) && c > highest) highest = c;
        return highest + 1;
    }
}
