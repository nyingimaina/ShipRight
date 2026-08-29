namespace ShipRight.Modules.Scheduler;

public static class TempoTimeZoneResolver
{
    public static TimeZoneInfo ResolveConfigured() =>
        Resolve(Environment.GetEnvironmentVariable("SHIPRIGHT__TIME_ZONE"));

    public static TimeZoneInfo Resolve(string? timeZoneId) =>
        string.IsNullOrWhiteSpace(timeZoneId)
            ? TimeZoneInfo.Utc
            : TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
}
