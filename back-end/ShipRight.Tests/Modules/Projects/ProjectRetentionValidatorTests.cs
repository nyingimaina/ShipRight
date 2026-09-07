using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Projects;

namespace ShipRight.Tests.Modules.Projects;

[TestClass]
public class ProjectRetentionValidatorTests
{
    private static ProjectConfig Project(params ServiceConfig[] services) => new()
    {
        Id = "p",
        Name = "Test",
        Services = services.ToList(),
    };

    private static ServiceConfig Service(int? retention = null, int? keep = null) => new()
    {
        Name = "api",
        DockerImageName = "org/app",
        ImageRetentionCount = retention,
        LocalImageKeepCount = keep,
    };

    private static T FieldValue<T>(object error, string propertyName)
    {
        var prop = error.GetType().GetProperty(propertyName)
            ?? throw new InvalidOperationException($"Property '{propertyName}' not found on error object.");
        return (T)prop.GetValue(error)!;
    }

    [TestMethod]
    public void Validate_DefaultsAndZeroes_NoErrors()
    {
        var errors = ProjectRetentionValidator.Validate(Project(Service(), Service(retention: 0, keep: 0)));
        Assert.AreEqual(0, errors.Count);
    }

    [TestMethod]
    public void Validate_MaxBoundaries_NoErrors()
    {
        var errors = ProjectRetentionValidator.Validate(Project(Service(retention: 100, keep: 100)));
        Assert.AreEqual(0, errors.Count);
    }

    [TestMethod]
    public void Validate_ImageRetentionNegative_AddsFieldError()
    {
        var errors = ProjectRetentionValidator.Validate(Project(Service(retention: -1)));
        Assert.AreEqual("services[0].imageRetentionCount", FieldValue<string>(errors.Single(), "field"));
    }

    [TestMethod]
    public void Validate_ImageRetentionAboveMax_AddsFieldError()
    {
        var errors = ProjectRetentionValidator.Validate(Project(Service(retention: 101)));
        Assert.AreEqual("services[0].imageRetentionCount", FieldValue<string>(errors.Single(), "field"));
    }

    [TestMethod]
    public void Validate_LocalKeepNegative_AddsFieldError()
    {
        var errors = ProjectRetentionValidator.Validate(Project(Service(keep: -1)));
        Assert.AreEqual("services[0].localImageKeepCount", FieldValue<string>(errors.Single(), "field"));
    }

    [TestMethod]
    public void Validate_ProjectCacheAboveMax_AddsFieldError()
    {
        var errors = ProjectRetentionValidator.Validate(Project(Service()) with { LocalCachePruneKeepGb = 101 });
        Assert.AreEqual("localCachePruneKeepGb", FieldValue<string>(errors.Single(), "field"));
    }

    [TestMethod]
    public void Validate_ProjectCacheNegative_AddsFieldError()
    {
        var errors = ProjectRetentionValidator.Validate(Project(Service()) with { LocalCachePruneKeepGb = -1 });
        Assert.AreEqual("localCachePruneKeepGb", FieldValue<string>(errors.Single(), "field"));
    }
}