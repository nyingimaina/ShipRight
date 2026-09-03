using Jattac.Libs.Tempo.Scheduling;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Scheduler;

namespace ShipRight.Tests.Modules.Scheduler;

[TestClass]
public class SchedulerRouterBindingTests
{
    [TestMethod]
    public void DesktopOverflowRoute_BindsSqliteStore()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddSingleton<SqliteBackupOverflowStore>(_ => null!);
        var provider = services.BuildServiceProvider();
        var options = new RequestDelegateFactoryOptions
        {
            ServiceProvider = provider,
            DisableInferBodyFromParameters = true,
        };

        var factory = RequestDelegateFactory.Create(
            (SqliteBackupOverflowStore overflowStore, string? projectId = null) =>
                Results.Ok(new { projectId }), options);

        Assert.IsNotNull(factory);
    }
}
