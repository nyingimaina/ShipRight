using Microsoft.VisualStudio.TestTools.UnitTesting;
using Jattac.Libs.Tempo.Scheduling;
using ShipRight.Modules.Scheduler;
using ShipRight.Shared.Time;

namespace ShipRight.Tests.Modules.Scheduling;

[TestClass]
public class TempoTimeZoneResolverTests
{
    [TestMethod]
    public void The_app_default_is_nairobi()
    {
        Assert.AreEqual("Africa/Nairobi", AppTimeZone.DefaultId);
        Assert.AreEqual(TimeSpan.FromHours(3), AppTimeZone.Default.GetUtcOffset(new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc)));
    }

    [TestMethod]
    public void Only_known_ids_are_offered_to_people()
    {
        Assert.IsTrue(AppTimeZone.IsKnown("Africa/Nairobi"));
        Assert.IsTrue(AppTimeZone.IsKnown("Europe/London"));
        Assert.IsFalse(AppTimeZone.IsKnown("Mars/Olympus_Mons"));
        Assert.IsFalse(AppTimeZone.IsKnown(""));
    }

    [TestMethod]
    public void Resolve_UsesConfiguredIanaTimeZone()
    {
        var timeZone = TempoTimeZoneResolver.Resolve("America/New_York");

        // Windows may report the equivalent Windows id; the offset is what matters.
        Assert.AreEqual(TimeSpan.FromHours(-4), timeZone.GetUtcOffset(new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc)));
    }

    [TestMethod]
    public void Resolve_DefaultsToTheAppZoneWhenConfigurationIsMissing()
    {
        var timeZone = TempoTimeZoneResolver.Resolve(null);

        Assert.AreEqual(AppTimeZone.Default.GetUtcOffset(new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc)),
            timeZone.GetUtcOffset(new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc)));
    }

    [TestMethod]
    public void Resolve_AnUnknownZoneFallsBackInsteadOfThrowing()
    {
        Assert.AreEqual(
            AppTimeZone.Default.GetUtcOffset(new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc)),
            TempoTimeZoneResolver.Resolve("Not/A-Timezone").GetUtcOffset(new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc)));
    }

    [TestMethod]
    public void CronSchedule_StoresConfiguredTimeZone()
    {
        var timeZone = TempoTimeZoneResolver.Resolve("Europe/Berlin");
        var schedule = new TempoSchedule.Cron("0 2 * * *", timeZone);

        Assert.AreEqual(timeZone.Id, schedule.TimeZone!.Id);
    }
}
