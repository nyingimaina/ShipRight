using Serilog;
using ShipRight.Shared.Time;

namespace ShipRight.Modules.Scheduler;

public static class TempoTimeZoneResolver
{
    /// <summary>
    /// The configured default zone, or Africa/Nairobi when none is set. A bad value is reported and the default is
    /// used: a scheduler that refuses to run is worse than one running in the app's zone while the setting is fixed.
    /// </summary>
    public static TimeZoneInfo ResolveConfigured()
    {
        var configured = Environment.GetEnvironmentVariable("SHIPRIGHT__TIME_ZONE");
        if (string.IsNullOrWhiteSpace(configured)) return AppTimeZone.Default;

        if (!AppTimeZone.TryResolve(configured, out var zone))
        {
            Log.Warning("SHIPRIGHT__TIME_ZONE '{Zone}' was not recognised; using {Default}.",
                configured, AppTimeZone.DefaultId);
            return AppTimeZone.Default;
        }

        return zone;
    }

    /// <summary>Accepts IANA ids from browsers and Windows ids, on any operating system; blank means the app default.</summary>
    public static TimeZoneInfo Resolve(string? timeZoneId) => AppTimeZone.Resolve(timeZoneId);
}
