using Microsoft.VisualStudio.TestTools.UnitTesting;
using Jattac.Libs.Tempo.Scheduling;
using ShipRight.Modules.Scheduler;

namespace ShipRight.Tests.Modules.Scheduling;

[TestClass]
public class TempoTimeZoneResolverTests
{
    [TestMethod]
    public void Resolve_UsesConfiguredIanaTimeZone()
    {
        var timeZone = TempoTimeZoneResolver.Resolve("America/New_York");

        Assert.AreEqual("America/New_York", timeZone.Id);
    }

    [TestMethod]
    public void Resolve_DefaultsToUtcWhenConfigurationIsMissing()
    {
        var timeZone = TempoTimeZoneResolver.Resolve(null);

        Assert.AreEqual(TimeZoneInfo.Utc.Id, timeZone.Id);
    }

    [TestMethod]
    public void Resolve_RejectsUnknownTimeZone()
    {
        Assert.ThrowsException<TimeZoneNotFoundException>(() =>
            TempoTimeZoneResolver.Resolve("Not/A-Timezone"));
    }

    [TestMethod]
    public void CronSchedule_StoresConfiguredTimeZone()
    {
        var timeZone = TempoTimeZoneResolver.Resolve("Europe/Berlin");
        var schedule = new TempoSchedule.Cron("0 2 * * *", timeZone);

        Assert.AreEqual(timeZone.Id, schedule.TimeZone!.Id);
    }
}
