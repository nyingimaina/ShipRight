using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Shared;

namespace ShipRight.Tests;

[TestClass]
public class LogRetentionTests
{
    [TestMethod]
    public void Logs_are_kept_for_seven_days()
    {
        Assert.AreEqual(7, LogRetention.Days);
    }

    [TestMethod]
    public void Server_and_desktop_both_use_the_shared_retention()
    {
        var server = File.ReadAllText(FindUp("back-end/ShipRight.Server/Program.cs"));
        Assert.IsTrue(server.Contains("retainedFileCountLimit: ShipRight.Shared.LogRetention.Days"), "server log retention must use LogRetention.Days");
        Assert.IsFalse(server.Contains("retainedFileCountLimit: 30"));
    }

    private static string FindUp(string rel)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, rel);
            if (File.Exists(p)) return p;
        }
        throw new FileNotFoundException(rel);
    }
}
