using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Database;

namespace ShipRight.Tests.Modules.Database;

[TestClass]
public class BackupRetentionTests
{
    private static BackupArtifactMeta Artifact(int day, int hour = 2, string? name = null) => new()
    {
        FileName = name ?? $"shop_2026-01-{day:00}_{hour:00}-00-00_manual.sql.zst.enc",
        CreatedAtUtc = new DateTimeOffset(2026, 1, day, hour, 0, 0, TimeSpan.Zero),
    };

    [TestMethod]
    public void Count_KeepsTheNewestNAndDeletesTheRest()
    {
        var artifacts = Enumerable.Range(1, 6).Select(d => Artifact(d)).ToList();

        var plan = BackupRetention.Plan(artifacts, new DateTimeOffset(2026, 1, 10, 0, 0, 0, TimeSpan.Zero),
            RetentionPolicy.From(3, 0));

        Assert.AreEqual(3, plan.Keep.Count);
        Assert.AreEqual(3, plan.Delete.Count);
        CollectionAssert.AreEqual(
            new[] { 6, 5, 4 },
            plan.Keep.Select(a => a.CreatedAtUtc.Day).ToArray());
    }

    [TestMethod]
    public void Age_DeletesOldFilesEvenWhenTheCountIsNotReached()
    {
        var artifacts = new[] { Artifact(1), Artifact(20), Artifact(30) };

        var plan = BackupRetention.Plan(artifacts, new DateTimeOffset(2026, 1, 31, 0, 0, 0, TimeSpan.Zero),
            RetentionPolicy.From(10, 14));

        var deleted = plan.Delete.Select(a => a.CreatedAtUtc.Day).OrderBy(d => d).ToArray();
        CollectionAssert.AreEqual(new[] { 1 }, deleted);
    }

    [TestMethod]
    public void TheNewestFile_IsNeverDeletedByAge()
    {
        var artifacts = new[] { Artifact(1) };

        var plan = BackupRetention.Plan(artifacts, new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero),
            RetentionPolicy.From(10, 1));

        Assert.AreEqual(1, plan.Keep.Count);
        Assert.AreEqual(0, plan.Delete.Count);
    }

    [TestMethod]
    public void ProjectedDeletion_UsesWhicheverRuleComesFirst()
    {
        var artifact = Artifact(10);
        var interval = TimeSpan.FromDays(1);

        var countWins = BackupRetention.ProjectedDeletion(artifact, rank: 2, RetentionPolicy.From(5, 30), interval);
        Assert.AreEqual(new DateTimeOffset(2026, 1, 14, 2, 0, 0, TimeSpan.Zero), countWins);

        var ageWins = BackupRetention.ProjectedDeletion(artifact, rank: 1, RetentionPolicy.From(30, 3), interval);
        Assert.AreEqual(new DateTimeOffset(2026, 1, 13, 2, 0, 0, TimeSpan.Zero), ageWins);
    }

    [TestMethod]
    public void ObservedInterval_IgnoresOneOffsAndUsesTheMedianGap()
    {
        var artifacts = new[] { Artifact(1), Artifact(2), Artifact(3), Artifact(4), Artifact(20) };

        var interval = BackupRetention.ObservedInterval(artifacts);

        Assert.AreEqual(TimeSpan.FromDays(1), interval);
    }
}
