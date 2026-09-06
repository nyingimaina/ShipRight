using Dapper.Contrib.Extensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Database;

namespace ShipRight.Tests.Database;

[TestClass]
public class QBuilderTableNameTests
{
    [Table("Build")]
    private class SampleBuildRecord
    {
        public Guid Id { get; set; }

        public int Deleted { get; set; }
    }

    private class UnannotatedRecord
    {
        public Guid Id { get; set; }
    }

    private class PlainType
    {
        public Guid Id { get; set; }
    }

    [TestMethod]
    public void NewQBuilder_UsesTableAttributeName_ForAnnotatedType()
    {
        using var qBuilder = AppQBuilderExtensions.NewQBuilder();
        var built = qBuilder
            .UseSelector()
            .Select<SampleBuildRecord>("Id")
            .Then()
            .BuildWithParameters();

        Assert.IsTrue(
            built.ParameterizedSql.Contains("From Build", StringComparison.OrdinalIgnoreCase),
            $"Expected SQL to reference table 'Build' but was: {built.ParameterizedSql}");
        Assert.IsFalse(
            built.ParameterizedSql.Contains("SampleBuildRecord"),
            $"Expected SQL to not reference the type name but was: {built.ParameterizedSql}");
    }

    [TestMethod]
    public void ResolveTableName_StripsRecordSuffix_ForUnannotatedType()
    {
        Assert.AreEqual("Unannotated", AppQBuilderExtensions.ResolveTableName(typeof(UnannotatedRecord)));
    }

    [TestMethod]
    public void ResolveTableName_ReturnsTypeName_ForPlainType()
    {
        Assert.AreEqual("PlainType", AppQBuilderExtensions.ResolveTableName(typeof(PlainType)));
    }

    [TestMethod]
    public void NewQBuilder_EmitsConfiguredTableName_ForTableBoundInsert()
    {
        using var qBuilder = AppQBuilderExtensions.NewQBuilder();
        var built = qBuilder
            .UseTableBoundInsert<SampleBuildRecord>()
            .FromObject(new SampleBuildRecord { Id = Guid.NewGuid() })
            .BuildWithParameters();

        Assert.IsFalse(
            built.ParameterizedSql.Contains("SampleBuildRecord"),
            $"Expected insert to reference table 'Build' but was: {built.ParameterizedSql}");
    }

    [TestMethod]
    public void SelectCountAs_EmitsUnqualifiedCountLiteral()
    {
        using var qBuilder = AppQBuilderExtensions.NewQBuilder();
        var built = qBuilder
            .SelectCountAs<SampleBuildRecord>("Count")
            .UseTableBoundFilter<SampleBuildRecord>()
            .WhereEqualTo(r => r.Deleted, 0)
            .Then()
            .BuildWithParameters();

        Assert.IsTrue(
            built.ParameterizedSql.Contains("COUNT(*) AS Count", StringComparison.OrdinalIgnoreCase),
            $"Expected unqualified COUNT(*) literal but was: {built.ParameterizedSql}");
        Assert.IsTrue(
            built.ParameterizedSql.Contains("From Build", StringComparison.OrdinalIgnoreCase),
            $"Expected table 'Build' but was: {built.ParameterizedSql}");
        Assert.IsFalse(
            built.ParameterizedSql.Contains("SampleBuildRecord"),
            $"Expected no type name in SQL but was: {built.ParameterizedSql}");
        Assert.IsFalse(
            built.ParameterizedSql.Contains(".COUNT"),
            $"Expected COUNT to not be table-qualified but was: {built.ParameterizedSql}");
    }
}
