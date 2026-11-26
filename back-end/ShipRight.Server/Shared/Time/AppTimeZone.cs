namespace ShipRight.Shared.Time;

/// <summary>
/// One place that knows the app's time zone rules. Browsers send IANA ids ('Africa/Nairobi'); Windows machines know
/// 'E. Africa Standard Time'. Both resolve here, on any operating system, with Nairobi as the considered default.
/// </summary>
public static class AppTimeZone
{
    public const string DefaultId = "Africa/Nairobi";

    public static TimeZoneInfo Default => Resolve(DefaultId);

    /// <summary>True only for an id this machine can actually use (directly or after IANA ↔ Windows conversion).</summary>
    public static bool TryResolve(string? id, out TimeZoneInfo zone)
    {
        zone = TimeZoneInfo.Utc;
        if (string.IsNullOrWhiteSpace(id)) return false;
        var trimmed = id.Trim();

        if (TimeZoneInfo.TryFindSystemTimeZoneById(trimmed, out var direct))
        {
            zone = direct;
            return true;
        }

        if (TimeZoneInfo.TryConvertIanaIdToWindowsId(trimmed, out var windows)
            && !string.IsNullOrWhiteSpace(windows)
            && TimeZoneInfo.TryFindSystemTimeZoneById(windows, out var fromIana))
        {
            zone = fromIana;
            return true;
        }

        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(trimmed, out var iana)
            && !string.IsNullOrWhiteSpace(iana)
            && TimeZoneInfo.TryFindSystemTimeZoneById(iana, out var fromWindows))
        {
            zone = fromWindows;
            return true;
        }

        return false;
    }

    /// <summary>Never throws: an unknown id falls back to the app default so one bad schedule cannot break a page.</summary>
    public static TimeZoneInfo Resolve(string? id)
    {
        if (TryResolve(id, out var zone)) return zone;
        if (TryResolve(DefaultId, out var fallback)) return fallback;
        return TimeZoneInfo.Utc;
    }

    /// <summary>The time zone to use for a person, falling back to the app default.</summary>
    public static string For(string? userTimeZone) =>
        TryResolve(userTimeZone, out _) ? userTimeZone!.Trim() : DefaultId;

    public static bool IsKnown(string? id) => TryResolve(id, out _);
}
